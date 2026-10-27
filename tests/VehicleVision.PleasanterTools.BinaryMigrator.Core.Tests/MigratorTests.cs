using System.Security.Cryptography;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class MigratorTests
{
    private static readonly string GuidA = new('A', 32);
    private static readonly string GuidB = new('B', 32);
    private static readonly string GuidC = new('C', 32);

    private static Task<MigrationReport> RunAsync(
        IBinarySource source,
        IBinarySink sink,
        MigratorOptions? options = null,
        IMigrationReporter? reporter = null,
        CancellationToken cancellationToken = default)
        => new Migrator(source, sink, options ?? TestData.Fast(), reporter).RunAsync(cancellationToken);

    // ---- 基本のコピーと検証 ----

    [Fact]
    public async Task コピーして_大きさとハッシュを確かめ_移行元は消さない()
    {
        var source = new FakeSource();
        var big = TestData.Bytes(300_000, 7); // バッファ（80 KiB）をまたぐ
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10, 1), contentType: "text/plain");
        source.Add(ObjectNames.Attachment(GuidB), []);
        source.Add(ObjectNames.Image(GuidC), big, contentType: "image/png");
        var sink = new FakeSink();

        var report = await RunAsync(source, sink);

        Assert.True(report.Succeeded);
        Assert.Equal(3, report.Copied);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.Equal(300_010, report.BytesCopied);
        Assert.Equal(big, sink.Objects[ObjectNames.Image(GuidC)]);
        Assert.Empty(sink.Objects[ObjectNames.Attachment(GuidB)]);
        Assert.Equal("image/png", sink.ContentTypes[ObjectNames.Image(GuidC)]);
        Assert.Empty(source.Deleted); // 既定では消さない
        Assert.True(sink.LastCheckForWrite);
    }

    [Fact]
    public async Task キーとオブジェクト名が違う移行元_Rds_のような_でも_オブジェクト名で書く()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(50), key: "12/Bin");
        source.Add(ObjectNames.SiteImage(5, SiteImageSize.Icon), TestData.Bytes(20), key: "13/Icon", contentType: "image/png");
        var sink = new FakeSink();

        var report = await RunAsync(source, sink);

        Assert.True(report.Succeeded);
        Assert.Contains(ObjectNames.Attachment(GuidA), sink.Objects.Keys);
        Assert.Contains("SiteImage/5_Icon.png", sink.Objects.Keys);
    }

    // ---- 4 方向相当の経路（実際の Local を使う） ----

    [Fact]
    public async Task Local_から_Local_への経路で_実ファイルが写り_検証される()
    {
        using var from = new TempFolder();
        using var to = new TempFolder();
        var data = TestData.Bytes(200_000, 3);
        from.Write(ObjectNames.Attachment(GuidA), data);
        from.Write(ObjectNames.SiteImage(9, SiteImageSize.Regular), TestData.Bytes(30));
        var source = new LocalBinaryStore(from.Path);
        var sink = new LocalBinaryStore(to.Path);

        var report = await RunAsync(source, sink);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.Copied);
        Assert.Equal(data, File.ReadAllBytes(System.IO.Path.Combine(to.Path, "Attachments", GuidA)));
        Assert.True(File.Exists(System.IO.Path.Combine(from.Path, "Attachments", GuidA))); // 移行元は残る
    }

    [Fact]
    public async Task Local_の移行元から_偽の_Azure_風の移行先_への経路で_削除は検証後だけ()
    {
        using var from = new TempFolder();
        from.Write(ObjectNames.Attachment(GuidA), TestData.Bytes(100));
        from.Write(ObjectNames.Attachment(GuidB), TestData.Bytes(100, 2));
        var sink = new FakeSink { Corrupt = (name, data) => name.EndsWith(GuidB, StringComparison.Ordinal) ? [.. data.Skip(1)] : data };

        var report = await RunAsync(new LocalBinaryStore(from.Path), sink, TestData.Fast(o => o with { DeleteSource = true }));

        Assert.Equal(1, report.Copied);
        Assert.Equal(1, report.Failed);
        Assert.False(File.Exists(System.IO.Path.Combine(from.Path, "Attachments", GuidA))); // 検証済み: 削除
        Assert.True(File.Exists(System.IO.Path.Combine(from.Path, "Attachments", GuidB)));  // 検証失敗: 残る
    }

    [Fact]
    public async Task 偽の移行元から_Local_の移行先への経路()
    {
        using var to = new TempFolder();
        var source = new FakeSource();
        var data = TestData.Bytes(1234);
        source.Add(ObjectNames.Image(GuidA), data);
        source.Add(ObjectNames.ImageThumbnail(GuidA), TestData.Bytes(12));

        var report = await RunAsync(source, new LocalBinaryStore(to.Path));

        Assert.True(report.Succeeded);
        Assert.Equal(data, File.ReadAllBytes(System.IO.Path.Combine(to.Path, "Images", GuidA)));
        Assert.True(File.Exists(System.IO.Path.Combine(to.Path, "Images", GuidA + "_thumbnail")));
        Assert.Empty(Directory.GetFiles(to.Path, "*" + LocalBinaryStore.TemporarySuffix, SearchOption.AllDirectories));
    }

    // ---- ドライラン ----

    [Fact]
    public async Task ドライランは_何も書かず_消さず_予定だけ数える()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        source.Add(ObjectNames.Attachment(GuidB), TestData.Bytes(10, 2));
        var sink = new FakeSink();
        sink.Objects[ObjectNames.Attachment(GuidB)] = TestData.Bytes(10, 2); // 同じ大きさがある

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { DryRun = true, DeleteSource = true }));

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.PlannedCopy);
        Assert.Equal(1, report.PlannedSkip);
        Assert.Equal(0, report.Copied);
        Assert.Equal(0, sink.WriteCount);
        Assert.Equal(0, sink.DeleteCount);
        Assert.Empty(source.Deleted);
        Assert.Equal(0, source.TotalOpens); // 本体は読まない
        Assert.False(sink.LastCheckForWrite); // ドライランでは保管先を作らない指定
        Assert.Single(sink.Objects);
    }

    // ---- 再開 ----

    [Fact]
    public async Task 同じ大きさと同じハッシュが移行先にあれば_とばす()
    {
        var source = new FakeSource();
        var data = TestData.Bytes(5000);
        source.Add(ObjectNames.Attachment(GuidA), data);
        var sink = new FakeSink();
        sink.Objects[ObjectNames.Attachment(GuidA)] = (byte[])data.Clone();

        var report = await RunAsync(source, sink);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Copied);
        Assert.Equal(0, sink.WriteCount);
    }

    [Fact]
    public async Task 大きさが同じでもハッシュが違えば_上書きしてコピーする()
    {
        var source = new FakeSource();
        var data = TestData.Bytes(5000, 1);
        source.Add(ObjectNames.Attachment(GuidA), data);
        var sink = new FakeSink();
        sink.Objects[ObjectNames.Attachment(GuidA)] = TestData.Bytes(5000, 2);

        var report = await RunAsync(source, sink);

        Assert.Equal(1, report.Copied);
        Assert.Equal(data, sink.Objects[ObjectNames.Attachment(GuidA)]);
    }

    [Fact]
    public async Task 途中で止めたあとの再実行は_済んだものをとばして残りだけコピーする()
    {
        var source = new FakeSource();
        for (var i = 0; i < 4; i++)
        {
            source.Add(ObjectNames.Attachment(new string((char)('A' + i), 32)), TestData.Bytes(100 + i, i));
        }
        var sink = new FakeSink
        {
            WriteFailure = (name, _) => name.EndsWith(new string('C', 32), StringComparison.Ordinal) ? new InvalidOperationException("boom") : null,
        };

        var first = await RunAsync(source, sink);
        Assert.Equal(3, first.Copied);
        Assert.Equal(1, first.Failed);

        sink.WriteFailure = null;
        var second = await RunAsync(source, sink);
        Assert.Equal(1, second.Copied);
        Assert.Equal(3, second.Skipped);
        Assert.True(second.Succeeded);
    }

    // ---- 整合の不一致 ----

    [Fact]
    public async Task 移行先が壊れていれば_失敗として数え_壊れたコピーを消し_移行元は消さない()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(100));
        var sink = new FakeSink { Corrupt = (_, data) => { var copy = (byte[])data.Clone(); copy[0] ^= 0xFF; return copy; } };

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { DeleteSource = true }));

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Copied);
        Assert.Contains("Verification failed", report.Failures[0].Error);
        Assert.Empty(source.Deleted);
        Assert.Empty(sink.Objects); // 壊れたコピーは残さない
    }

    [Fact]
    public async Task 移行元が報告した大きさと読めた大きさが違えば_失敗()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(100), reportedSize: 120);
        var sink = new FakeSink();

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { DeleteSource = true }));

        Assert.Equal(1, report.Failed);
        Assert.Contains("size", report.Failures[0].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(source.Deleted);
    }

    // ---- --delete-source ----

    [Fact]
    public async Task 削除は_検証済みだけ_コピー済み_とばした_検証失敗_読めない_を混ぜて確かめる()
    {
        var source = new FakeSource();
        var okData = TestData.Bytes(64, 1);
        source.Add(ObjectNames.Attachment(GuidA), okData);                  // 新規コピー: 削除する
        source.Add(ObjectNames.Attachment(GuidB), TestData.Bytes(64, 2));   // すでに同一: とばして削除する
        source.Add(ObjectNames.Attachment(GuidC), TestData.Bytes(64, 3));   // 検証失敗: 削除しない
        var unreadable = new string('D', 32);
        source.Add(ObjectNames.Attachment(unreadable), TestData.Bytes(64, 4)); // 読めない: 削除しない
        source.OpenFailures[ObjectNames.Attachment(unreadable)] = () => new InvalidOperationException("unreadable");
        var sink = new FakeSink
        {
            Corrupt = (name, data) => name.EndsWith(GuidC, StringComparison.Ordinal) ? TestData.Bytes(data.Length, 99) : data,
        };
        sink.Objects[ObjectNames.Attachment(GuidB)] = TestData.Bytes(64, 2);

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { DeleteSource = true }));

        Assert.Equal(1, report.Copied);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(2, report.Failed);
        Assert.Equal(2, report.SourceDeleted);
        Assert.Equivalent(new[] { ObjectNames.Attachment(GuidA), ObjectNames.Attachment(GuidB) }, source.Deleted.ToArray());
    }

    [Fact]
    public async Task 移行元の削除に失敗しても_コピーは残り_失敗として数える()
    {
        var source = new FakeSource { DeleteFailure = _ => new InvalidOperationException("denied") };
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink();

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { DeleteSource = true }));

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.SourceDeleted);
        Assert.Contains("could not be deleted", report.Failures[0].Error);
        Assert.True(sink.Objects.ContainsKey(ObjectNames.Attachment(GuidA)));
    }

    [Fact]
    public void 削除できない移行元に_DeleteSource_を指定すると_拒む()
        => Assert.Throws<ArgumentException>(
            () => new Migrator(new ReadOnlyFakeSource(), new FakeSink(), new MigratorOptions { DeleteSource = true }));

    // ---- 失敗の扱い ----

    [Fact]
    public async Task 一件の失敗で全体を止めず_集計して最後に非成功にする()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10, 1));
        source.Add(ObjectNames.Attachment(GuidB), TestData.Bytes(10, 2));
        source.Add(ObjectNames.Attachment(GuidC), TestData.Bytes(10, 3));
        source.OpenFailures[ObjectNames.Attachment(GuidB)] = () => new InvalidOperationException("cannot read");
        var sink = new FakeSink();
        var reporter = new RecordingReporter();

        var report = await RunAsync(source, sink, reporter: reporter);

        Assert.False(report.Succeeded);
        Assert.False(report.Aborted);
        Assert.Equal(2, report.Copied);
        Assert.Equal(1, report.Failed);
        Assert.Equal(3, reporter.Results.Count);
        Assert.Equal(ObjectNames.Attachment(GuidB), report.Failures[0].Item.ObjectName);
    }

    [Fact]
    public async Task 移行元の記録に問題があれば_コピーせず失敗とする()
    {
        var source = new FakeSource();
        source.AddProblem("Binaries/BinaryId=7", "The Guid is bad.");
        var sink = new FakeSink();

        var report = await RunAsync(source, sink);

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, sink.WriteCount);
        Assert.Equal("The Guid is bad.", report.Failures[0].Error);
    }

    [Fact]
    public async Task 全件に効く失敗_Fatal_は_即座に止め_残りを処理しない()
    {
        var source = new FakeSource();
        for (var i = 0; i < 5; i++)
        {
            source.Add(ObjectNames.Attachment(new string((char)('A' + i), 32)), TestData.Bytes(10, i));
        }
        var sink = new FakeSink { WriteFailure = (_, _) => new FatalMigrationException("Azure authentication failed.") };

        var report = await RunAsync(source, sink);

        Assert.True(report.Aborted);
        Assert.False(report.Succeeded);
        Assert.Equal("Azure authentication failed.", report.AbortReason);
        Assert.Equal(1, sink.WriteCount); // 最初の 1 件で止まる
        Assert.Equal(0, report.Total);
        Assert.Equal(1, source.TotalOpens);
    }

    [Fact]
    public async Task 事前の確認で接続できなければ_1件も処理せず止める()
    {
        var source = new FakeSource { CheckFailure = new FatalMigrationException("Cannot connect to the database.") };
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink();

        var report = await RunAsync(source, sink);

        Assert.True(report.Aborted);
        Assert.Equal("Cannot connect to the database.", report.AbortReason);
        Assert.Equal(0, source.TotalOpens);
        Assert.Equal(0, sink.WriteCount);
    }

    [Fact]
    public async Task 移行先の事前確認の失敗も_止める()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink { CheckFailure = new FatalMigrationException("The Azure container does not exist.") };

        var report = await RunAsync(source, sink);

        Assert.True(report.Aborted);
        Assert.Equal(0, sink.WriteCount);
    }

    // ---- 再試行 ----

    [Fact]
    public async Task 一時的な失敗は_少数回だけ再試行して成功する()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink { WriteFailure = (_, attempt) => attempt <= 2 ? new IOException("network") : null };
        var reporter = new RecordingReporter();

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { MaxRetries = 3 }), reporter);

        Assert.True(report.Succeeded);
        Assert.Equal(1, report.Copied);
        Assert.Equal(3, sink.WriteCount);
        Assert.Equal(2, reporter.Retries.Count);
    }

    [Fact]
    public async Task 再試行の上限を超えたら_失敗()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink { WriteFailure = (_, _) => new IOException("network") };

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { MaxRetries = 2 }));

        Assert.Equal(1, report.Failed);
        Assert.Equal(3, sink.WriteCount); // 最初 + 再試行 2 回
    }

    [Fact]
    public async Task 一時的でない失敗は_再試行しない()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        var sink = new FakeSink { WriteFailure = (_, _) => new ArgumentException("bad name") };

        var report = await RunAsync(source, sink);

        Assert.Equal(1, report.Failed);
        Assert.Equal(1, sink.WriteCount);
    }

    // ---- 同時実行・中断 ----

    [Fact]
    public async Task 並列を指定しても_全件が正しく写る()
    {
        var source = new FakeSource();
        var expected = new Dictionary<string, byte[]>();
        for (var i = 0; i < 40; i++)
        {
            var name = ObjectNames.Attachment(i.ToString("X32"));
            var data = TestData.Bytes(1000 + i, i);
            source.Add(name, data);
            expected[name] = data;
        }
        var sink = new FakeSink();

        var report = await RunAsync(source, sink, TestData.Fast(o => o with { Parallel = 4 }));

        Assert.True(report.Succeeded);
        Assert.Equal(40, report.Copied);
        foreach (var (name, data) in expected)
        {
            Assert.Equal(data, sink.Objects[name]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void 並列数の範囲外は拒む(int parallel)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new Migrator(new FakeSource(), new FakeSink(), new MigratorOptions { Parallel = parallel }));

    [Fact]
    public async Task 中断されたら_途中で止めて_中断として記録する()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(GuidA), TestData.Bytes(10));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await RunAsync(source, new FakeSink(), cancellationToken: cts.Token);

        Assert.True(report.Aborted);
        Assert.False(report.Succeeded);
        Assert.Equal("Interrupted.", report.AbortReason);
    }

    // ---- ハッシュの前提 ----

    [Fact]
    public async Task ハッシュは_SHA256_の小文字16進で_ストリームから求める()
    {
        var data = TestData.Bytes(250_000, 5);
        await using var stream = new MemoryStream(data);

        var fingerprint = await BinaryHash.ComputeAsync(stream);

        Assert.Equal(data.Length, fingerprint.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), fingerprint.Sha256);
    }
}
