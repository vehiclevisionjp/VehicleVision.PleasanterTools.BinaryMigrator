using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class HashingStreamTests
{
    [Fact]
    public async Task 読んだ分の大きさとSHA256を求め_末尾に達したことを知らせる()
    {
        var data = TestData.Bytes(200_000, 11);
        await using var hashing = new HashingReadStream(new MemoryStream(data));

        await hashing.CopyToAsync(Stream.Null);

        Assert.Equal(data.Length, hashing.BytesRead);
        Assert.True(hashing.ReachedEnd);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), hashing.GetSha256());
    }

    [Fact]
    public async Task 途中までしか読まなければ_末尾に達していない()
    {
        await using var hashing = new HashingReadStream(new MemoryStream(TestData.Bytes(1000)));
        var buffer = new byte[10];

        await hashing.ReadExactlyAsync(buffer);

        Assert.Equal(10, hashing.BytesRead);
        Assert.False(hashing.ReachedEnd);
    }

    [Fact]
    public async Task 空のストリームのハッシュ()
    {
        await using var hashing = new HashingReadStream(new MemoryStream());
        await hashing.CopyToAsync(Stream.Null);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), hashing.GetSha256());
        Assert.True(hashing.ReachedEnd);
    }

    [Fact]
    public void シークと書き込みはできない()
    {
        using var hashing = new HashingReadStream(new MemoryStream([1]));
        Assert.False(hashing.CanSeek);
        Assert.False(hashing.CanWrite);
        Assert.Throws<NotSupportedException>(() => hashing.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => hashing.Seek(0, SeekOrigin.Begin));
    }
}
