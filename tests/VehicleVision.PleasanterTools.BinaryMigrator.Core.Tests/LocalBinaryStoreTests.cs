namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class LocalBinaryStoreTests
{
    private const string GuidA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static async Task<List<SourceObject>> ListAsync(LocalBinaryStore store)
    {
        var list = new List<SourceObject>();
        await foreach (var item in store.EnumerateAsync(CancellationToken.None))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task 列挙は_Pleasanterの外部ストレージの配置_Attachments_Images_SiteImage_TenantImage_をたどる()
    {
        using var folder = new TempFolder();
        folder.Write("Attachments/" + GuidA, TestData.Bytes(10));
        folder.Write("Images/" + GuidA, TestData.Bytes(20));
        folder.Write("Images/" + GuidA + "_thumbnail", TestData.Bytes(5));
        folder.Write("SiteImage/3_Regular.png", TestData.Bytes(7));
        folder.Write("TenantImage/1_Logo.png", TestData.Bytes(8));
        folder.Write("Other/x.bin", TestData.Bytes(9)); // 対象外のフォルダ
        folder.Write("Attachments/ignored" + LocalBinaryStore.TemporarySuffix, TestData.Bytes(1)); // 書き込み途中の一時ファイル

        var items = await ListAsync(new LocalBinaryStore(folder.Path));

        Assert.Equal(
            ["Attachments/" + GuidA, "Images/" + GuidA, "Images/" + GuidA + "_thumbnail", "SiteImage/3_Regular.png", "TenantImage/1_Logo.png"],
            items.Select(i => i.ObjectName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(10, items.Single(i => i.ObjectName == "Attachments/" + GuidA).Size);
        Assert.All(items, i => Assert.Null(i.Problem));
    }

    [Fact]
    public async Task 種別を絞ると_そのフォルダだけを列挙する()
    {
        using var folder = new TempFolder();
        folder.Write("Attachments/" + GuidA, TestData.Bytes(10));
        folder.Write("Images/" + GuidA, TestData.Bytes(20));

        var items = await ListAsync(new LocalBinaryStore(folder.Path, [BinaryGroup.Images]));

        Assert.Equal(["Images/" + GuidA], items.Select(i => i.ObjectName).ToArray());
    }

    [Fact]
    public async Task 存在しないルートは_移行元としては致命的な失敗()
    {
        var store = new LocalBinaryStore(Path.Combine(Path.GetTempPath(), "bm-missing-" + Guid.NewGuid().ToString("N")));
        await Assert.ThrowsAsync<FatalMigrationException>(() => ((IBinarySource)store).CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 移行先としての確認は_ドライランでは作らず_書き込みでは作る()
    {
        using var parent = new TempFolder();
        var root = Path.Combine(parent.Path, "storage");
        var store = new LocalBinaryStore(root);

        await ((IBinarySink)store).CheckAsync(forWrite: false, CancellationToken.None);
        Assert.False(Directory.Exists(root));

        await ((IBinarySink)store).CheckAsync(forWrite: true, CancellationToken.None);
        Assert.True(Directory.Exists(root));
        Assert.Empty(Directory.GetFileSystemEntries(root)); // 書き込みの確認用ファイルは残さない
    }

    [Fact]
    public async Task 書き込みと読み戻しと削除()
    {
        using var folder = new TempFolder();
        var store = new LocalBinaryStore(folder.Path);
        var data = TestData.Bytes(150_000, 4);
        var name = "Attachments/" + GuidA;

        Assert.Null(await store.GetSizeAsync(name, CancellationToken.None));
        await store.WriteAsync(name, new MemoryStream(data), "application/pdf", CancellationToken.None);

        Assert.Equal(data.Length, await store.GetSizeAsync(name, CancellationToken.None));
        var back = await store.ReadBackAsync(name, CancellationToken.None);
        Assert.Equal(data.Length, back.Size);
        Assert.Equal((await BinaryHash.ComputeAsync(new MemoryStream(data))).Sha256, back.Sha256);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder.Path, "Attachments", GuidA)));

        await store.DeleteAsync(name, CancellationToken.None);
        Assert.Null(await store.GetSizeAsync(name, CancellationToken.None));
        await store.DeleteAsync(name, CancellationToken.None); // 無くても何もしない
    }

    [Fact]
    public async Task 書き込みの途中で失敗しても_正規の名前に中途半端なファイルを残さない()
    {
        using var folder = new TempFolder();
        var store = new LocalBinaryStore(folder.Path);
        var name = "Attachments/" + GuidA;
        await store.WriteAsync(name, new MemoryStream(TestData.Bytes(100, 1)), null, CancellationToken.None); // 既存

        await Assert.ThrowsAsync<IOException>(() => store.WriteAsync(name, new FailingStream(), null, CancellationToken.None));

        Assert.Equal(TestData.Bytes(100, 1), File.ReadAllBytes(Path.Combine(folder.Path, "Attachments", GuidA))); // 既存は無傷
        Assert.Empty(Directory.GetFiles(folder.Path, "*" + LocalBinaryStore.TemporarySuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 既存を置き換えて書ける()
    {
        using var folder = new TempFolder();
        var store = new LocalBinaryStore(folder.Path);
        var name = "Images/" + GuidA;
        await store.WriteAsync(name, new MemoryStream(TestData.Bytes(10, 1)), null, CancellationToken.None);
        await store.WriteAsync(name, new MemoryStream(TestData.Bytes(20, 2)), null, CancellationToken.None);
        Assert.Equal(20, await store.GetSizeAsync(name, CancellationToken.None));
    }

    // ---- パストラバーサルの拒否 ----

    public static TheoryData<string> BadNames() =>
    [
        "../outside.txt",
        "Attachments/../../outside.txt",
        "/abs/outside.txt",
        "C:/outside.txt",
        "\\\\host\\share\\outside.txt",
        "Attachments\\..\\x",
        "",
    ];

    [Theory]
    [MemberData(nameof(BadNames))]
    public async Task 危険なオブジェクト名は_全ての操作で拒み_ルートの外へ何も作らない(string name)
    {
        using var parent = new TempFolder();
        var root = Path.Combine(parent.Path, "storage");
        Directory.CreateDirectory(root);
        var store = new LocalBinaryStore(root);

        Assert.Throws<ArgumentException>(() => store.ResolvePath(name));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetSizeAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(name, new MemoryStream([1, 2, 3]), null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadBackAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.OpenReadAsync(new SourceObject(name, 0, name), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteSourceAsync(new SourceObject(name, 0, name), CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(parent.Path, "outside.txt")));
        Assert.Empty(Directory.GetFiles(parent.Path, "outside.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 外へ出る名前を移行元から渡されても_移行先は書かず_失敗として数える()
    {
        using var parent = new TempFolder();
        var root = Path.Combine(parent.Path, "storage");
        var source = new FakeSource();
        source.Add("../escape.bin", TestData.Bytes(10));
        source.Add("Attachments/" + GuidA, TestData.Bytes(10));

        var report = await new Migrator(source, new LocalBinaryStore(root), TestData.Fast()).RunAsync();

        Assert.Equal(1, report.Copied);
        Assert.Equal(1, report.Failed);
        Assert.False(File.Exists(Path.Combine(parent.Path, "escape.bin")));
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ > 0)
            {
                throw new IOException("simulated failure");
            }
            Array.Fill(buffer, (byte)1, offset, Math.Min(count, 10));
            return Math.Min(count, 10);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, temp.Length);
            temp.AsMemory(0, read).CopyTo(buffer);
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
