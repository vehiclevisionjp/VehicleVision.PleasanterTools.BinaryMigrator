using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// AzureBlob への接続の指定。認証は、接続文字列があればそれを、無ければ <c>DefaultAzureCredential</c> を使う。
/// 接続文字列は秘密のため、ログやコンソールへ出さない。
/// </summary>
/// <param name="ContainerName">コンテナー名（Pleasanter の <c>AzureBlobContainerName</c> に当たる）。</param>
/// <param name="AccountUri">ストレージアカウントの URI（Pleasanter の <c>AzureBlobStorageAccountUri</c> に当たる）。接続文字列を使うときは不要。</param>
/// <param name="ConnectionString">接続文字列（任意）。</param>
public sealed record AzureBlobConnection(string ContainerName, Uri? AccountUri, string? ConnectionString)
{
    /// <summary>指定の誤りを調べる。問題があれば理由（英語）、無ければ null。</summary>
    public string? FindProblem()
    {
        if (string.IsNullOrWhiteSpace(ContainerName))
        {
            return "The Azure container name is required.";
        }
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            if (AccountUri is null)
            {
                return "Specify the Azure account URI, or provide an Azure connection string.";
            }
            if (!AccountUri.IsAbsoluteUri || AccountUri.Scheme is not ("https" or "http"))
            {
                return "The Azure account URI must be an absolute http(s) URI.";
            }
        }
        return null;
    }
}

/// <summary>
/// AzureBlob の保管先。移行元・移行先・移行元の削除のすべてを担う。
/// オブジェクト名はそのままブロブ名（<c>Attachments/{Guid}</c> など）。
/// </summary>
public sealed class AzureBlobBinaryStore : IBinarySource, IBinarySink, ISourceDeleter
{
    private const int ReadBufferSize = 4 * 1024 * 1024;
    private const string DefaultContentType = "application/octet-stream";

    private readonly BlobContainerClient _container;
    private readonly IReadOnlyList<BinaryGroup> _groups;

    /// <summary>テストなどで、作成済みのクライアントを渡す。</summary>
    internal AzureBlobBinaryStore(BlobContainerClient container, IReadOnlyList<BinaryGroup>? groups = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _groups = groups ?? BinaryGroups.All;
    }

    /// <summary>接続の指定からクライアントを作る（ネットワークには接続しない）。</summary>
    public static AzureBlobBinaryStore Create(AzureBlobConnection connection, IReadOnlyList<BinaryGroup>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var problem = connection.FindProblem();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(connection));
        }

        // 再試行は SDK の組み込みの仕組みにも任せる（一時的な失敗は少数回）
        var options = new BlobClientOptions
        {
            Retry = { MaxRetries = 3, Mode = RetryMode.Exponential, Delay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(30) },
        };

        try
        {
            BlobContainerClient container;
            if (!string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                container = new BlobContainerClient(connection.ConnectionString, connection.ContainerName, options);
            }
            else
            {
                var service = new BlobServiceClient(connection.AccountUri!, new DefaultAzureCredential(), options);
                container = service.GetBlobContainerClient(connection.ContainerName);
            }
            return new AzureBlobBinaryStore(container, groups);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
        {
            // 例外の文言に接続文字列が混ざらないよう、固定の文言にする
            throw new FatalMigrationException("The Azure connection settings are invalid.");
        }
    }

    public string DisplayName => "AzureBlob (account " + _container.AccountName + ", container " + _container.Name + ")";

    // ---- 接続の確認 ----

    public Task CheckAsync(CancellationToken cancellationToken) => CheckContainerAsync(cancellationToken);

    public Task CheckAsync(bool forWrite, CancellationToken cancellationToken) => CheckContainerAsync(cancellationToken);

    private async Task CheckContainerAsync(CancellationToken cancellationToken)
    {
        // コンテナーは作らない（Pleasanter の設定と権限の管理は利用者に任せる）
        await RunAsync(async () =>
        {
            await _container.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    // ---- 移行元 ----

    public async IAsyncEnumerable<SourceObject> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var group in _groups)
        {
            foreach (var prefix in ObjectNames.Prefixes(group))
            {
                var pages = _container
                    .GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (await MoveNextAsync(pages).ConfigureAwait(false))
                    {
                        var blob = pages.Current;
                        if (blob.Name.EndsWith('/'))
                        {
                            continue; // 階層名前空間のディレクトリ
                        }
                        yield return new SourceObject(
                            blob.Name,
                            blob.Properties.ContentLength ?? 0,
                            blob.Name,
                            blob.Properties.ContentType,
                            Problem: ObjectNames.FindProblem(blob.Name) is { } problem
                                ? "Invalid blob name: " + problem
                                : null);
                    }
                }
                finally
                {
                    await pages.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<BlobItem> pages)
    {
        try
        {
            return await pages.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ToFatal(ex) is { } fatal)
        {
            throw fatal;
        }
    }

    public Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var blob = _container.GetBlobClient(item.ObjectName);
            // 読み取りの途中でブロブが変わったら、ETag の不一致で失敗する（壊れた読み取りを防ぐ）
            return await blob.OpenReadAsync(
                new BlobOpenReadOptions(allowModifications: false) { BufferSize = ReadBufferSize },
                cancellationToken).ConfigureAwait(false);
        });

    public Task DeleteSourceAsync(SourceObject item, CancellationToken cancellationToken)
        => DeleteAsync(item.ObjectName, cancellationToken);

    // ---- 移行先 ----

    public Task<long?> GetSizeAsync(string objectName, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            try
            {
                var properties = await _container.GetBlobClient(RequireName(objectName))
                    .GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return (long?)properties.Value.ContentLength;
            }
            catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode != BlobErrorCode.ContainerNotFound)
            {
                return null;
            }
        });

    public Task WriteAsync(string objectName, Stream content, string? contentType, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var blob = _container.GetBlobClient(RequireName(objectName));
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType,
                },
            };
            // ブロブへの書き込みは、全体が揃ってから確定する（途中で失敗しても、中途半端なブロブは残らない）
            await blob.UploadAsync(content, options, cancellationToken).ConfigureAwait(false);
            return true;
        });

    public async Task<BinaryFingerprint> ReadBackAsync(string objectName, CancellationToken cancellationToken)
    {
        var stream = await RunAsync(async () => await _container.GetBlobClient(RequireName(objectName))
            .OpenReadAsync(new BlobOpenReadOptions(allowModifications: false) { BufferSize = ReadBufferSize }, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await BinaryHash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DeleteAsync(string objectName, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            await _container.GetBlobClient(RequireName(objectName))
                .DeleteIfExistsAsync(DeleteSnapshotsOption.None, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        });

    private static string RequireName(string objectName)
    {
        ObjectNames.Validate(objectName);
        return objectName;
    }

    // ---- 例外の分類 ----

    private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ToFatal(ex) is { } fatal)
        {
            throw fatal;
        }
    }

    /// <summary>認証・権限・コンテナーの不在など、全件に効く失敗なら <see cref="FatalMigrationException"/> へ写す。</summary>
    internal static FatalMigrationException? ToFatal(Exception exception)
    {
        switch (exception)
        {
            case AuthenticationFailedException:
                // CredentialUnavailableException を含む。詳細メッセージに環境の情報が混ざり得るため、固定の文言にする
                return new FatalMigrationException(
                    "Azure authentication failed. Sign in (for example, az login) or set the environment variables used by DefaultAzureCredential, "
                    + "or provide an Azure connection string.");
            case RequestFailedException { Status: 401 or 403 } denied:
                return new FatalMigrationException(
                    $"Azure denied the request (HTTP {denied.Status}, {denied.ErrorCode}). Check the account, the container and the permissions.");
            case RequestFailedException { ErrorCode: "ContainerNotFound" }:
                return new FatalMigrationException("The Azure container does not exist. Create it first.");
            case RequestFailedException { ErrorCode: "AccountIsDisabled" or "InvalidAuthenticationInfo" } invalid:
                return new FatalMigrationException($"Azure rejected the account or the credentials ({invalid.ErrorCode}).");
            default:
                return null;
        }
    }
}
