using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>大きさと SHA-256（小文字 16 進）。整合の確認に使う。</summary>
public readonly record struct BinaryFingerprint(long Size, string Sha256);

/// <summary>SHA-256 の計算。常にストリームで読み、全体をメモリへ載せない。</summary>
public static class BinaryHash
{
    private const int BufferSize = 81920;

    public static async Task<BinaryFingerprint> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        return new BinaryFingerprint(total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

/// <summary>
/// 読みながら大きさと SHA-256 を求めるストリーム（コピー中の計算用）。読み取り専用で、シークできない。
/// </summary>
public sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _result;

    public HashingReadStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>ここまでに読んだバイト数。</summary>
    public long BytesRead { get; private set; }

    /// <summary>末尾まで読み終えたか。</summary>
    public bool ReachedEnd { get; private set; }

    /// <summary>ここまでに読んだ分の SHA-256。取得後はそれ以上読めない。</summary>
    public string GetSha256()
        => _result ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private void Account(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            ReachedEnd = true;
            return;
        }
        _hash.AppendData(data);
        BytesRead += data.Length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _hash.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
