namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// 移行元（読む側）。Rds・Local・AzureBlob の実装を差し替えられるようにする抽象。
/// 読み取りだけを行い、移行元を変更しない（削除は <see cref="ISourceDeleter"/> が別に担う）。
/// </summary>
public interface IBinarySource
{
    /// <summary>コンソールへ出す名前（秘密を含めない）。</summary>
    string DisplayName { get; }

    /// <summary>
    /// 接続・認証・存在を確かめる。全件に効く失敗（接続できない・認証できない）は
    /// <see cref="FatalMigrationException"/> で知らせる。
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken);

    /// <summary>移行対象を列挙する。全件を一度にメモリへ載せず、順に返す。</summary>
    IAsyncEnumerable<SourceObject> EnumerateAsync(CancellationToken cancellationToken);

    /// <summary>本体を先頭から順に読むストリームを開く。呼び出し側が破棄する。</summary>
    Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken);

    /// <summary>SHA-256（小文字 16 進）を、ストリームで読んで求める。</summary>
    async Task<string> ComputeHashAsync(SourceObject item, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(item, cancellationToken).ConfigureAwait(false);
        return (await BinaryHash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false)).Sha256;
    }
}

/// <summary>
/// 移行元の削除（明示のオプションのときだけ使う）。
/// Rds は <c>Bin</c> などを NULL にする更新、Local・AzureBlob はオブジェクトの削除。
/// </summary>
public interface ISourceDeleter
{
    Task DeleteSourceAsync(SourceObject item, CancellationToken cancellationToken);
}
