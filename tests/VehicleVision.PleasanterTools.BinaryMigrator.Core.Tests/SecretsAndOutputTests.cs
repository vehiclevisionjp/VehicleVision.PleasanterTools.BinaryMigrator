namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class SecretsAndOutputTests
{
    private static string? Resolve(SecretSource source, string? env = null, string? stdin = null, string? fileText = null)
        => SecretResolver.Resolve(
            source,
            "SECRET_ENV",
            name => name == "SECRET_ENV" ? env : null,
            () => new StringReader(stdin ?? string.Empty),
            _ => fileText ?? throw new FileNotFoundException());

    [Fact]
    public void 環境変数から読む()
        => Assert.Equal("abc", Resolve(SecretSource.None, env: "  abc\n"));

    [Fact]
    public void ファイルが環境変数より優先_前後の空白と改行は除く()
        => Assert.Equal("from-file", Resolve(new SecretSource(File: "x.txt"), env: "from-env", fileText: "from-file\r\n"));

    [Fact]
    public void 標準入力から読む()
        => Assert.Equal("from-stdin", Resolve(new SecretSource(FromStdin: true), env: "from-env", stdin: "from-stdin\n"));

    [Fact]
    public void 空なら_null()
    {
        Assert.Null(Resolve(SecretSource.None, env: "  "));
        Assert.Null(Resolve(SecretSource.None));
        Assert.Null(Resolve(new SecretSource(FromStdin: true), stdin: "\n"));
    }

    [Fact]
    public void ファイルが読めなければ_パスだけを示し_中身は出さない()
    {
        var ex = Assert.Throws<FatalMigrationException>(() => Resolve(new SecretSource(File: "missing.txt")));
        Assert.Contains("missing.txt", ex.Message);
    }

    // ---- 秘密の伏せ字 ----

    [Fact]
    public void 接続文字列そのものと_その中のパスワードを伏せる()
    {
        var redactor = new SecretRedactor();
        redactor.Add("Server=db;User Id=sa;Password=S3cretPw!;Encrypt=true");

        var text = redactor.Redact("failed: Server=db;User Id=sa;Password=S3cretPw!;Encrypt=true / login for sa failed with S3cretPw!");

        Assert.DoesNotContain("S3cretPw", text);
        Assert.Contains("***", text);
    }

    [Fact]
    public void 接続文字列の形をした文言は_登録がなくても伏せる()
    {
        var redactor = new SecretRedactor();
        var text = redactor.Redact("error near 'Password=hunter2hunter2;Host=x' and AccountKey=abcDEF123==; sig=zzz");

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abcDEF", text);
        Assert.DoesNotContain("zzz", text);
        Assert.Contains("Host=x", text);
    }

    [Fact]
    public void Azureの接続文字列のキーを伏せる()
    {
        var redactor = new SecretRedactor();
        redactor.Add("DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=a2V5a2V5a2V5a2V5;EndpointSuffix=core.windows.net");

        var text = redactor.Redact("The key a2V5a2V5a2V5a2V5 was rejected");

        Assert.DoesNotContain("a2V5a2V5", text);
    }

    [Fact]
    public void 短すぎる秘密や空は登録しない_普通の文章を壊さない()
    {
        var redactor = new SecretRedactor();
        redactor.Add("");
        redactor.Add(null);
        redactor.Add("ab");
        Assert.Equal("a plain message about abc", redactor.Redact("a plain message about abc"));
    }

    // ---- 表示 ----

    [Fact]
    public void 表示は_ASCIIだけ_日本語は問いかけ記号になる()
    {
        Assert.Equal("a?b c", ReportFormatter.ToAscii("a日b\nc"));
        Assert.Equal("plain text", ReportFormatter.ToAscii("plain text"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(5L * 1024 * 1024, "5 MiB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GiB")]
    public void 大きさの表示(long bytes, string expected)
        => Assert.Equal(expected, ReportFormatter.FormatSize(bytes));

    [Fact]
    public async Task 集計の表示には_件数と失敗の一覧と_とばした数が含まれ_全て英語のASCII()
    {
        var source = new FakeSource();
        source.Add(ObjectNames.Attachment(new string('A', 32)), TestData.Bytes(10, 1));
        source.Add(ObjectNames.Attachment(new string('B', 32)), TestData.Bytes(10, 2));
        source.Add(ObjectNames.Attachment(new string('C', 32)), TestData.Bytes(10, 3));
        source.OpenFailures[ObjectNames.Attachment(new string('C', 32))] = () => new InvalidOperationException("読めません");
        var sink = new FakeSink();
        sink.Objects[ObjectNames.Attachment(new string('B', 32))] = TestData.Bytes(10, 2);

        var report = await new Migrator(source, sink, TestData.Fast()).RunAsync();
        var lines = ReportFormatter.FormatSummary(report);
        var text = string.Join("\n", lines);

        Assert.Contains("Copied           : 1", text);
        Assert.Contains("Skipped          : 1", text);
        Assert.Contains("Failed           : 1", text);
        Assert.Contains("Failures", text);
        Assert.Contains(new string('C', 32), text);
        Assert.Contains("Result: NOT all succeeded", text);
        Assert.Contains("BinaryStorage.json was not modified", text);
        Assert.True(text.All(c => c is >= ' ' and <= '~' or '\n'));
    }

    [Fact]
    public void ドライランの集計は_何も書いていないことを示す()
    {
        var report = new MigrationReport(dryRun: true, deleteSource: true);
        report.Add(new ItemResult(new SourceObject("Attachments/x", 5, "k"), ItemOutcome.PlannedCopy, WouldDeleteSource: true));

        var text = string.Join("\n", ReportFormatter.FormatSummary(report));

        Assert.Contains("Dry run", text);
        Assert.Contains("Would copy       : 1", text);
        Assert.Contains("Would delete src", text);
        Assert.Contains("Result: success.", text);
    }

    [Fact]
    public void 中断の表示には_理由と未処理があることを含める()
    {
        var report = new MigrationReport(false, false);
        report.Abort("Azure authentication failed.");

        var text = string.Join("\n", ReportFormatter.FormatSummary(report));

        Assert.Contains("STOPPED: Azure authentication failed.", text);
        Assert.Contains("NOT all succeeded", text);
    }

    [Fact]
    public void 一件の表示()
    {
        var item = new SourceObject("Attachments/" + new string('A', 32), 2048, "k");
        Assert.Equal(
            "[3] COPIED Attachments/" + new string('A', 32) + " (2 KiB) source-deleted",
            ReportFormatter.FormatItem(3, new ItemResult(item, ItemOutcome.Copied, SourceDeleted: true)));
        Assert.Contains(": boom", ReportFormatter.FormatItem(4, new ItemResult(item, ItemOutcome.Failed, Error: "boom")));
        Assert.Contains("WOULD-COPY", ReportFormatter.FormatItem(5, new ItemResult(item, ItemOutcome.PlannedCopy)));
    }

    // ---- 計画の組み立て（接続しない） ----

    [Fact]
    public void 計画は_接続文字列を秘密として登録し_表示名に出さない()
    {
        var options = new MigrateCommandOptions
        {
            From = StorageKind.Rds,
            To = StorageKind.Local,
            RdsDbms = RdsDbms.PostgreSql,
            LocalPath = Path.GetTempPath(),
            DeleteSource = true,
            Yes = true,
            Parallel = 3,
            MaxRetries = 5,
        };

        var plan = MigrationPlan.Create(
            options,
            name => name == EnvironmentNames.RdsConnectionString ? "Host=db;Password=pw-very-secret" : null,
            () => TextReader.Null,
            _ => throw new InvalidOperationException());

        Assert.DoesNotContain("pw-very-secret", plan.Source.DisplayName);
        Assert.DoesNotContain("pw-very-secret", plan.Redactor.Redact("oops Host=db;Password=pw-very-secret"));
        Assert.True(plan.Options.DeleteSource);
        Assert.Equal(3, plan.Options.Parallel);
        Assert.Equal(5, plan.Options.MaxRetries);
        Assert.IsType<LocalBinaryStore>(plan.Sink);
    }

    [Fact]
    public void 計画は_接続文字列が空なら_作らない()
    {
        var options = new MigrateCommandOptions { From = StorageKind.Rds, To = StorageKind.Local, RdsDbms = RdsDbms.MySql, LocalPath = "x" };
        Assert.Throws<FatalMigrationException>(
            () => MigrationPlan.Create(options, _ => null, () => TextReader.Null, _ => string.Empty));
    }

    [Fact]
    public void 計画は_Azureのアカウント_URI_が絶対URIでなければ作らない()
    {
        var options = new MigrateCommandOptions
        {
            From = StorageKind.Local,
            To = StorageKind.Azure,
            LocalPath = "x",
            AzureContainer = "c",
            AzureAccountUri = "not a uri",
        };
        Assert.Throws<FatalMigrationException>(
            () => MigrationPlan.Create(options, _ => null, () => TextReader.Null, _ => string.Empty));
    }
}
