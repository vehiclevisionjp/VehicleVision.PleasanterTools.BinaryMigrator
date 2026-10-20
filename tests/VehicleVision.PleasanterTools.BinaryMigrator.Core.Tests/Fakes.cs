using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

/// <summary>メモリ上の偽の移行元。Rds のように、キーとオブジェクト名が違う場合も試せる。</summary>
internal sealed class FakeSource : IBinarySource, ISourceDeleter
{
    private readonly List<(SourceObject Item, byte[] Data)> _items = [];
    private readonly ConcurrentDictionary<string, int> _openCounts = new();

    public ConcurrentBag<string> Deleted { get; } = [];

    /// <summary>この名前の読み取りを開くとき、投げる例外。</summary>
    public Dictionary<string, Func<Exception>> OpenFailures { get; } = [];

    public Func<SourceObject, Exception?>? DeleteFailure { get; set; }

    public Exception? CheckFailure { get; set; }

    public string DisplayName => "fake source";

    public int OpenCount(string objectName) => _openCounts.GetValueOrDefault(objectName);

    public int TotalOpens => _openCounts.Values.Sum();

    public SourceObject Add(string objectName, byte[] data, string? key = null, string? contentType = null, long? reportedSize = null)
    {
        var item = new SourceObject(objectName, reportedSize ?? data.Length, key ?? objectName, contentType);
        _items.Add((item, data));
        return item;
    }

    public void AddProblem(string objectName, string problem)
        => _items.Add((new SourceObject(objectName, 0, objectName, Problem: problem), []));

    public Task CheckAsync(CancellationToken cancellationToken)
        => CheckFailure is null ? Task.CompletedTask : Task.FromException(CheckFailure);

    public async IAsyncEnumerable<SourceObject> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        foreach (var (item, _) in _items.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken)
    {
        _openCounts.AddOrUpdate(item.ObjectName, 1, (_, n) => n + 1);
        if (OpenFailures.TryGetValue(item.ObjectName, out var failure))
        {
            return Task.FromException<Stream>(failure());
        }
        var data = _items.First(i => i.Item.Key == item.Key).Data;
        return Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    public Task DeleteSourceAsync(SourceObject item, CancellationToken cancellationToken)
    {
        var failure = DeleteFailure?.Invoke(item);
        if (failure is not null)
        {
            return Task.FromException(failure);
        }
        Deleted.Add(item.ObjectName);
        return Task.CompletedTask;
    }
}

/// <summary>読むだけで削除できない移行元。</summary>
internal sealed class ReadOnlyFakeSource : IBinarySource
{
    public string DisplayName => "read-only source";

    public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async IAsyncEnumerable<SourceObject> EnumerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }

    public Task<Stream> OpenReadAsync(SourceObject item, CancellationToken cancellationToken)
        => Task.FromResult<Stream>(new MemoryStream());
}

/// <summary>メモリ上の偽の移行先。わざと壊す・失敗させる指定ができる。</summary>
internal sealed class FakeSink : IBinarySink
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public ConcurrentDictionary<string, string?> ContentTypes { get; } = new();

    private int _writeCount;
    private int _deleteCount;
    private int _checkCount;

    public int WriteCount => _writeCount;

    public int DeleteCount => _deleteCount;

    public bool? LastCheckForWrite { get; private set; }

    public Exception? CheckFailure { get; set; }

    /// <summary>書き込んだ内容を、保存する前に変える（壊す）。</summary>
    public Func<string, byte[], byte[]>? Corrupt { get; set; }

    /// <summary>書き込みの前に、この名前で呼ばれて例外を返す（null なら成功）。引数は何回目の書き込みか（1 から）。</summary>
    public Func<string, int, Exception?>? WriteFailure { get; set; }

    private readonly ConcurrentDictionary<string, int> _writeAttempts = new();

    public string DisplayName => "fake sink";

    public Task CheckAsync(bool forWrite, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _checkCount);
        LastCheckForWrite = forWrite;
        return CheckFailure is null ? Task.CompletedTask : Task.FromException(CheckFailure);
    }

    public Task<long?> GetSizeAsync(string objectName, CancellationToken cancellationToken)
        => Task.FromResult<long?>(Objects.TryGetValue(objectName, out var data) ? data.Length : null);

    public async Task WriteAsync(string objectName, Stream content, string? contentType, CancellationToken cancellationToken)
    {
        var attempt = _writeAttempts.AddOrUpdate(objectName, 1, (_, n) => n + 1);
        Interlocked.Increment(ref _writeCount);
        var failure = WriteFailure?.Invoke(objectName, attempt);
        if (failure is not null)
        {
            throw failure;
        }
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        Objects[objectName] = Corrupt is null ? data : Corrupt(objectName, data);
        ContentTypes[objectName] = contentType;
    }

    public Task<BinaryFingerprint> ReadBackAsync(string objectName, CancellationToken cancellationToken)
    {
        if (!Objects.TryGetValue(objectName, out var data))
        {
            throw new FileNotFoundException(objectName);
        }
        return Task.FromResult(new BinaryFingerprint(data.Length, Convert.ToHexStringLower(SHA256.HashData(data))));
    }

    public Task DeleteAsync(string objectName, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _deleteCount);
        Objects.TryRemove(objectName, out _);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingReporter : IMigrationReporter
{
    public ConcurrentBag<ItemResult> Results { get; } = [];

    public ConcurrentBag<(string Name, int Attempt)> Retries { get; } = [];

    public void ItemCompleted(long sequence, ItemResult result) => Results.Add(result);

    public void Retrying(string objectName, int attempt, int maxRetries, string reason) => Retries.Add((objectName, attempt));
}

internal static class TestData
{
    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static MigratorOptions Fast(Func<MigratorOptions, MigratorOptions>? configure = null)
    {
        var options = new MigratorOptions { RetryDelay = TimeSpan.Zero };
        return configure is null ? options : configure(options);
    }
}
