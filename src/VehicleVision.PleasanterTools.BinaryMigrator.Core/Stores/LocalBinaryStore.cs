using System.Runtime.CompilerServices;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// Local（フォルダ）の保管先。移行元・移行先・移行元の削除のすべてを担う。
/// ルートは Pleasanter の <c>BinaryStorage.json</c> の <c>Path</c>（無ければ <c>App_Data/BinaryStorage</c>）に当たる。
/// オブジェクト名の <c>/</c> をディレクトリ区切りへ写す。ルートの外へ出る名前は拒む。
/// </summary>
public sealed class LocalBinaryStore : IBinarySource, IBinarySink, ISourceDeleter
{
    /// <summary>書き込み中の一時ファイルの接尾辞。完了したら正規の名前へ置き換える。</summary>
    internal const string TemporarySuffix = ".migrating.tmp";

    private const string WriteProbePrefix = ".binary-migrator-write-probe-";
    private const int BufferSize = 81920;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly IReadOnlyList<BinaryGroup> _groups;

    public LocalBinaryStore(string rootPath, IReadOnlyList<BinaryGroup>? groups = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("The local folder path is empty.", nameof(rootPath));
        }
        _root = Path.GetFullPath(rootPath);
        _rootWithSeparator = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        _groups = groups ?? BinaryGroups.All;
    }

    public string RootPath => _root;

    public string DisplayName => "Local folder (" + _root + ")";

    /// <summary>
    /// オブジェクト名を、ルートの下の実パスへ写す。検証を通らない名前、ルートの外へ出る名前は <see cref="ArgumentException"/>。
    /// </summary>
    public string ResolvePath(string objectName)
    {
        var relative = ObjectNames.ToRelativePath(objectName);
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_rootWithSeparator, PathComparison))
        {
            throw new ArgumentException("The object name resolves outside the storage folder.", nameof(objectName));
        }
        return full;
    }

    // ---- 移行元 ----

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            throw new FatalMigrationException("The local folder does not exist: " + _root);
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SourceObject> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // シンボリックリンク等はたどらない（保管先の外へ出ないように）
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = false,
        };
        foreach (var group in _groups)
        {
            foreach (var prefix in ObjectNames.Prefixes(group))
            {
                var directory = new DirectoryInfo(Path.Combine(_root, prefix.TrimEnd('/')));
                if (!directory.Exists)
                {
                    continue;
                }
                foreach (var file in directory.EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file.Name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var objectName = ObjectNames.FromRelativePath(Path.GetRelativePath(_root, file.FullName));
                    yield return new SourceObject(
                        objectName,
                        file.Length,
                        objectName,
                        Problem: ObjectNames.FindProblem(objectName) is { } problem
                            ? "Invalid file name in the local folder: " + problem
                            : null);
                }
            }
        }
    }

    public Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(OpenRead(ResolvePath(item.ObjectName)));

    public Task DeleteSourceAsync(SourceObject item, CancellationToken cancellationToken)
    {
        File.Delete(ResolvePath(item.ObjectName));
        return Task.CompletedTask;
    }

    // ---- 移行先 ----

    public Task CheckAsync(bool forWrite, CancellationToken cancellationToken)
    {
        if (!forWrite)
        {
            return Task.CompletedTask;
        }
        try
        {
            Directory.CreateDirectory(_root);
            var probe = Path.Combine(_root, WriteProbePrefix + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FatalMigrationException("The local folder is not writable: " + _root + " (" + ex.GetType().Name + ")", ex);
        }
        return Task.CompletedTask;
    }

    public Task<long?> GetSizeAsync(string objectName, CancellationToken cancellationToken)
    {
        var info = new FileInfo(ResolvePath(objectName));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public async Task WriteAsync(string objectName, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        var path = ResolvePath(objectName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + TemporarySuffix;
        try
        {
            await using (var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            // 完成したものだけを正規の名前にする（途中で止まっても、中途半端なファイルを正規の名前で残さない）
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // 後始末の失敗は元の例外を優先する
            }
            throw;
        }
    }

    public async Task<BinaryFingerprint> ReadBackAsync(string objectName, CancellationToken cancellationToken)
    {
        await using var stream = OpenRead(ResolvePath(objectName));
        return await BinaryHash.ComputeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string objectName, CancellationToken cancellationToken)
    {
        File.Delete(ResolvePath(objectName));
        return Task.CompletedTask;
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
}
