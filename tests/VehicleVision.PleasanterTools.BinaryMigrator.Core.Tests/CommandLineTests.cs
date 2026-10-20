namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

public sealed class CommandLineTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    private static ParsedCommand Parse(string[] args, params (string Name, string Value)[] env)
        => CommandLine.Parse(args, Env(env));

    private static MigrateCommandOptions Ok(string[] args, params (string Name, string Value)[] env)
    {
        var parsed = Parse(args, env);
        Assert.True(parsed.Kind == CommandKind.Migrate, parsed.Error);
        return parsed.Options!;
    }

    private static string Error(string[] args, params (string Name, string Value)[] env)
    {
        var parsed = Parse(args, env);
        Assert.Equal(CommandKind.Error, parsed.Kind);
        return parsed.Error!;
    }

    private static readonly (string, string) RdsSecret = (EnvironmentNames.RdsConnectionString, "Server=db;Password=pw12345");
    private static readonly (string, string) AzureUri = (EnvironmentNames.AzureAccountUri, "https://acct.blob.core.windows.net");

    // ---- ヘルプ・版 ----

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void ヘルプ(string flag)
        => Assert.Equal(CommandKind.Help, Parse([flag]).Kind);

    [Fact]
    public void コマンドの後ろの_help_も_ヘルプ()
        => Assert.Equal(CommandKind.Help, Parse(["migrate", "--help"]).Kind);

    [Fact]
    public void 版()
        => Assert.Equal(CommandKind.Version, Parse(["--version"]).Kind);

    [Fact]
    public void ヘルプの文面は_英語で_主要なオプションを含む()
    {
        Assert.All(
            ["--from", "--to", "--dry-run", "--delete-source", "--yes", "--parallel", "--types", "--rds-dbms",
             "--local-path", "--azure-container", "BINARY_MIGRATOR_RDS_CONNECTION_STRING", "Exit codes"],
            expected => Assert.Contains(expected, CommandLine.HelpText));
        var bad = CommandLine.HelpText.Where(c => !(c is >= ' ' and <= '~' or '\n')).Select(c => ((int)c).ToString()).Distinct().ToArray();
        Assert.True(bad.Length == 0, "ヘルプはコンソールへ出すため ASCII だけにする: " + string.Join(",", bad));
    }

    // ---- 誤り ----

    [Fact]
    public void 引数なしは誤り() => Assert.Contains("No command", Error([]));

    [Fact]
    public void 未知のコマンドは誤り() => Assert.Contains("Unknown command", Error(["copy"]));

    [Fact]
    public void 未知のオプションは誤り() => Assert.Contains("--bogus", Error(["migrate", "--from", "local", "--to", "azure", "--bogus"]));

    [Fact]
    public void 値の無いオプションは誤り() => Assert.Contains("needs a value", Error(["migrate", "--from"]));

    [Fact]
    public void from_と_to_は必須() => Assert.Contains("required", Error(["migrate", "--from", "local"]));

    [Fact]
    public void Rdsへの書き戻しは_対象外() => Assert.Contains("Rds is not supported", Error(["migrate", "--from", "local", "--to", "rds"]));

    [Fact]
    public void 同じ保管先どうしは誤り() => Assert.Contains("must differ", Error(["migrate", "--from", "local", "--to", "local", "--local-path", "x"]));

    [Fact]
    public void 未知の保管先は誤り()
    {
        Assert.Contains("Unknown --from", Error(["migrate", "--from", "ftp", "--to", "local"]));
        Assert.Contains("Unknown --to", Error(["migrate", "--from", "local", "--to", "ftp"]));
    }

    [Theory]
    [InlineData("--rds-connection-string")]
    [InlineData("--azure-connection-string")]
    public void 接続文字列を引数へ直接書くことは_拒む_パスワードが見えるため(string option)
    {
        var message = Error(["migrate", "--from", "rds", "--to", "local", option, "Server=x;Password=leak"]);
        Assert.Contains("not supported", message);
        Assert.DoesNotContain("leak", message);
    }

    // ---- 4 方向の解析 ----

    [Fact]
    public void Rds_から_Local()
    {
        var o = Ok(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "postgresql", "--local-path", "/data/bin"], RdsSecret);

        Assert.Equal(StorageKind.Rds, o.From);
        Assert.Equal(StorageKind.Local, o.To);
        Assert.Equal(RdsDbms.PostgreSql, o.RdsDbms);
        Assert.Equal("/data/bin", o.LocalPath);
        Assert.False(o.DryRun);
        Assert.False(o.DeleteSource);
        Assert.Equal(1, o.Parallel);
        Assert.Equal(3, o.MaxRetries);
        Assert.Equal(BinaryGroups.All, o.Types);
    }

    [Fact]
    public void Rds_から_Azure_環境変数で_DBMS_接続文字列_コンテナー_URI()
    {
        var o = Ok(
            ["migrate", "--from", "rds", "--to", "azure"],
            RdsSecret, (EnvironmentNames.RdsDbms, "sqlserver"), (EnvironmentNames.AzureContainer, "pleasanter"), AzureUri);

        Assert.Equal(RdsDbms.SqlServer, o.RdsDbms);
        Assert.Equal("pleasanter", o.AzureContainer);
        Assert.Equal("https://acct.blob.core.windows.net", o.AzureAccountUri);
    }

    [Fact]
    public void Local_から_Azure()
    {
        var o = Ok(["migrate", "--from", "local", "--to", "azure", "--local-path", "D:\\bin", "--azure-container", "c", "--azure-account-uri", "https://a.blob.core.windows.net"]);
        Assert.Equal(StorageKind.Local, o.From);
        Assert.Equal(StorageKind.Azure, o.To);
    }

    [Fact]
    public void Azure_から_Local_接続文字列は環境変数()
    {
        var o = Ok(
            ["migrate", "--from", "azure", "--to", "local", "--local-path", "/x", "--azure-container", "c"],
            (EnvironmentNames.AzureConnectionString, "UseDevelopmentStorage=true"));
        Assert.Equal(StorageKind.Azure, o.From);
    }

    [Fact]
    public void イコールで値を渡せる_引数は環境変数より優先()
    {
        var o = Ok(
            ["migrate", "--from=local", "--to=azure", "--local-path=/arg", "--azure-container=c", "--parallel=4"],
            (EnvironmentNames.LocalPath, "/env"), AzureUri);
        Assert.Equal("/arg", o.LocalPath);
        Assert.Equal(4, o.Parallel);
    }

    // ---- 必須の指定 ----

    [Fact]
    public void Rdsは_DBMS_が必要()
        => Assert.Contains("DBMS", Error(["migrate", "--from", "rds", "--to", "local", "--local-path", "x"], RdsSecret));

    [Fact]
    public void 未対応の_DBMS_は誤り()
        => Assert.Contains("Unknown --rds-dbms", Error(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "oracle", "--local-path", "x"], RdsSecret));

    [Fact]
    public void Rdsは_接続文字列の取得元が必要()
        => Assert.Contains("connection string", Error(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "mysql", "--local-path", "x"]));

    [Fact]
    public void Rdsの接続文字列は_ファイルでも標準入力でもよい_両方は不可()
    {
        var withFile = Ok(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "mysql", "--local-path", "x", "--rds-connection-string-file", "c.txt"]);
        Assert.Equal("c.txt", withFile.RdsConnectionString.File);

        var withStdin = Ok(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "mysql", "--local-path", "x", "--rds-connection-string-stdin"]);
        Assert.True(withStdin.RdsConnectionString.FromStdin);

        Assert.Contains("only one", Error(
            ["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "mysql", "--local-path", "x",
             "--rds-connection-string-file", "c.txt", "--rds-connection-string-stdin"]));
    }

    [Fact]
    public void Localは_パスが必要()
        => Assert.Contains("--local-path", Error(["migrate", "--from", "local", "--to", "azure", "--azure-container", "c"], AzureUri));

    [Fact]
    public void Azureは_コンテナーが必要()
        => Assert.Contains("container", Error(["migrate", "--from", "local", "--to", "azure", "--local-path", "x"], AzureUri));

    [Fact]
    public void Azureは_アカウントURIか接続文字列のどちらかが必要()
        => Assert.Contains("Azure access", Error(["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c"]));

    [Fact]
    public void 標準入力は_1つの秘密にしか使えない()
        => Assert.Contains("Standard input", Error(
            ["migrate", "--from", "rds", "--to", "azure", "--rds-dbms", "mysql", "--azure-container", "c",
             "--rds-connection-string-stdin", "--azure-connection-string-stdin"]));

    // ---- 数値・種別 ----

    [Theory]
    [InlineData("0")]
    [InlineData("17")]
    [InlineData("x")]
    [InlineData("-1")]
    public void 並列数の範囲外は誤り(string value)
        => Assert.Contains("--parallel", Error(["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c", "--parallel", value], AzureUri));

    [Theory]
    [InlineData("11")]
    [InlineData("-1")]
    public void 再試行回数の範囲外は誤り(string value)
        => Assert.Contains("--max-retries", Error(["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c", "--max-retries", value], AzureUri));

    [Fact]
    public void 種別の絞り込み()
    {
        var o = Ok(["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c", "--types", "images,attachments"], AzureUri);
        Assert.Equal([BinaryGroup.Images, BinaryGroup.Attachments], o.Types);
        Assert.Contains("videos", Error(["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c", "--types", "videos"], AzureUri));
    }

    // ---- ドライランと削除 ----

    private static readonly string[] LocalToAzure = ["migrate", "--from", "local", "--to", "azure", "--local-path", "x", "--azure-container", "c"];

    [Fact]
    public void 既定では移行元を消さない()
        => Assert.False(Ok(LocalToAzure, AzureUri).DeleteSource);

    [Fact]
    public void delete_source_は_yes_が無ければ止まる()
    {
        var message = Error([.. LocalToAzure, "--delete-source"], AzureUri);
        Assert.Contains("--yes", message);
        Assert.Contains("DELETES", message);
    }

    [Fact]
    public void Rdsの_delete_source_の確認では_NULLにすることを示す()
        => Assert.Contains("NULL", Error(["migrate", "--from", "rds", "--to", "local", "--rds-dbms", "mysql", "--local-path", "x", "--delete-source"], RdsSecret));

    [Theory]
    [InlineData("--yes")]
    [InlineData("-y")]
    public void yes_があれば_delete_source_を受ける(string yes)
    {
        var o = Ok([.. LocalToAzure, "--delete-source", yes], AzureUri);
        Assert.True(o.DeleteSource);
        Assert.True(o.Yes);
    }

    [Fact]
    public void ドライランでは_yes_なしで_delete_source_を指定できる()
    {
        var o = Ok([.. LocalToAzure, "--dry-run", "--delete-source"], AzureUri);
        Assert.True(o.DryRun);
        Assert.True(o.DeleteSource);
    }

    [Fact]
    public void 値を取らないフラグに値を付けると誤り()
        => Assert.Contains("does not take a value", Error([.. LocalToAzure, "--dry-run=true"], AzureUri));

    [Fact]
    public void quiet_と_parallel_と_max_retries()
    {
        var o = Ok([.. LocalToAzure, "--quiet", "--parallel", "8", "--max-retries", "0"], AzureUri);
        Assert.True(o.Quiet);
        Assert.Equal(8, o.Parallel);
        Assert.Equal(0, o.MaxRetries);
    }
}
