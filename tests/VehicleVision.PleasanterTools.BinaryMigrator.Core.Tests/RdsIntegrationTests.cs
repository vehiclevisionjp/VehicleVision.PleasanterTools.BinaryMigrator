using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

/// <summary>
/// 実際の DB（SQL Server・PostgreSQL・MySQL）へつなぐ結合テスト。Pleasanter の本物の表は使わず、
/// <c>Binaries</c> 表を模した小さな表を作って、<see cref="RdsBinarySource"/> の読み取り・削除と、<see cref="Migrator"/> を通した移行を確かめる。
/// 接続先は環境変数（<c>BM_TEST_SQLSERVER</c>・<c>BM_TEST_POSTGRESQL</c>・<c>BM_TEST_MYSQL</c>）から読む。
/// 設定が無い DBMS のテストは何もせずに終わる。CI では <c>BM_REQUIRE_ALL_RDBMS=1</c> を立て、未設定なら落とす
/// （<see cref="CIでは全RDBMSが設定されている"/>）。表は 1 つのため、全テストを同じコレクションで直列に回す。
/// </summary>
[Collection("RdsIntegration")]
public sealed class RdsIntegrationTests
{
    private const string Guid1 = "0123456789ABCDEF0123456789ABCDEF";
    private const string Guid2 = "FEDCBA9876543210FEDCBA9876543210";
    private const string Guid3 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static string VariableName(RdsDbms dbms) => dbms switch
    {
        RdsDbms.SqlServer => "BM_TEST_SQLSERVER",
        RdsDbms.PostgreSql => "BM_TEST_POSTGRESQL",
        RdsDbms.MySql => "BM_TEST_MYSQL",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    private static string? ConnectionStringOf(RdsDbms dbms)
    {
        var value = Environment.GetEnvironmentVariable(VariableName(dbms));
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static TheoryData<RdsDbms> Dbmses => new(Enum.GetValues<RdsDbms>());

    [Fact]
    public void CIでは全RDBMSが設定されている()
    {
        if (Environment.GetEnvironmentVariable("BM_REQUIRE_ALL_RDBMS") != "1")
        {
            return;
        }
        foreach (var dbms in Enum.GetValues<RdsDbms>())
        {
            Assert.True(ConnectionStringOf(dbms) is not null, VariableName(dbms) + " is not set. Without it, the integration tests silently do nothing.");
        }
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 表と列が読める(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        var source = new RdsBinarySource(connection);

        await source.CheckAsync(CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 表が無ければ_致命的な失敗にする(RdsDbms dbms)
    {
        var text = ConnectionStringOf(dbms);
        if (text is null)
        {
            return;
        }
        await ExecuteAsync(dbms, text, "DROP TABLE IF EXISTS " + RdsSql.Quote(dbms, "Binaries"));
        var source = new RdsBinarySource(new RdsConnection(dbms, text));

        await Assert.ThrowsAsync<FatalMigrationException>(() => source.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 一覧は_頁をまたいで_対象の行だけを返し_本体を同じバイト列で読める(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        var data = Seed();
        await InsertAllAsync(dbms, connection.ConnectionString, data);
        // 頁の大きさ 2 で、頁送りを複数回通す
        var source = new RdsBinarySource(connection, pageSize: 2);

        var items = await ListAsync(source);

        Assert.Equal(
            [
                "Attachments/" + Guid1,
                "Images/" + Guid2,
                "SiteImage/321_Regular.png",
                "SiteImage/321_Thumbnail.png",
                "SiteImage/321_Icon.png",
                "TenantImage/7_Logo.png",
                "Attachments/" + Guid3,
            ],
            items.Select(i => i.ObjectName).ToArray());
        Assert.All(items, i => Assert.Null(i.Problem));
        foreach (var item in items)
        {
            Assert.Equal(data.Expected[item.ObjectName].Length, item.Size);
            await using var stream = await source.OpenReadAsync(item, CancellationToken.None);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            Assert.Equal(data.Expected[item.ObjectName], memory.ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 種別を絞ると_その種別だけを返す(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        await InsertAllAsync(dbms, connection.ConnectionString, Seed());
        var source = new RdsBinarySource(connection, [BinaryGroup.Images]);

        var items = await ListAsync(source);

        Assert.Equal(["Images/" + Guid2], items.Select(i => i.ObjectName).ToArray());
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 読み取り用のセッションでは_更新できない(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        await InsertAllAsync(dbms, connection.ConnectionString, Seed());
        var statements = RdsSql.ReadOnlySessionStatements(dbms);
        if (statements.Count == 0)
        {
            return; // SQL Server は接続文字列の ApplicationIntent に頼る（セッションの指定は無い）
        }

        await using var db = Open(dbms, connection.ConnectionString);
        await db.OpenAsync();
        foreach (var statement in statements)
        {
            await using var set = db.CreateCommand();
            set.CommandText = statement;
            await set.ExecuteNonQueryAsync();
        }
        await using var update = db.CreateCommand();
        update.CommandText = RdsSql.ClearColumn(dbms, RdsColumn.Bin);
        AddLong(update, "@id", 5);
        AddLong(update, "@size", 5);

        await Assert.ThrowsAnyAsync<DbException>(() => update.ExecuteNonQueryAsync());
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 5, "Bin"));
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task 移行元の削除は_長さが一致する行の列だけをNULLにする(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        await InsertAllAsync(dbms, connection.ConnectionString, Seed());
        var source = new RdsBinarySource(connection);
        var items = await ListAsync(source);
        var attachment = items.Single(i => i.ObjectName == "Attachments/" + Guid1);
        var siteThumbnail = items.Single(i => i.ObjectName == "SiteImage/321_Thumbnail.png");

        // 読んだときと長さが違えば、更新されない
        await Assert.ThrowsAsync<MigrationIntegrityException>(
            () => source.DeleteSourceAsync(attachment with { Size = attachment.Size + 1 }, CancellationToken.None));
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 1, "Bin"));

        await source.DeleteSourceAsync(attachment, CancellationToken.None);
        await source.DeleteSourceAsync(siteThumbnail, CancellationToken.None);

        Assert.Null(await ReadColumnAsync(dbms, connection.ConnectionString, 1, "Bin"));
        Assert.Null(await ReadColumnAsync(dbms, connection.ConnectionString, 3, "Thumbnail"));
        // 同じ行のほかの列と、ほかの行は残る
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 3, "Bin"));
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 3, "Icon"));
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 2, "Bin"));
        // すでに NULL の列は、もう一度消せない
        await Assert.ThrowsAsync<MigrationIntegrityException>(() => source.DeleteSourceAsync(attachment, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Dbmses))]
    public async Task Migratorを通して_Rdsから_Localへ移し_検証した移行元だけを消す(RdsDbms dbms)
    {
        if (await PrepareAsync(dbms) is not { } connection)
        {
            return;
        }
        var data = Seed();
        await InsertAllAsync(dbms, connection.ConnectionString, data);
        using var folder = new TempFolder();
        var source = new RdsBinarySource(connection, pageSize: 3);
        var sink = new LocalBinaryStore(folder.Path);

        var report = await new Migrator(
            source,
            sink,
            new MigratorOptions { DeleteSource = true, Parallel = 2, RetryDelay = TimeSpan.Zero }).RunAsync();

        Assert.False(report.Aborted);
        Assert.Equal(0, report.Failed);
        Assert.Equal(data.Expected.Count, report.Copied);
        Assert.Equal(data.Expected.Count, report.SourceDeleted);
        foreach (var (name, bytes) in data.Expected)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(sink.ResolvePath(name)));
        }
        // 対象の列はすべて NULL になり、対象外の行（Temporary）は触られない
        Assert.Null(await ReadColumnAsync(dbms, connection.ConnectionString, 1, "Bin"));
        Assert.Null(await ReadColumnAsync(dbms, connection.ConnectionString, 3, "Icon"));
        Assert.NotNull(await ReadColumnAsync(dbms, connection.ConnectionString, 90, "Bin"));
    }

    // ---- 準備 ----

    private sealed record SeedData(IReadOnlyList<SeedRow> Rows, IReadOnlyDictionary<string, byte[]> Expected);

    private sealed record SeedRow(long BinaryId, long ReferenceId, string? Guid, string BinaryType, byte[]? Bin, byte[]? Thumbnail = null, byte[]? Icon = null);

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task<List<SourceObject>> ListAsync(RdsBinarySource source)
    {
        var items = new List<SourceObject>();
        await foreach (var item in source.EnumerateAsync(CancellationToken.None))
        {
            items.Add(item);
        }
        return items;
    }

    private static SeedData Seed()
    {
        // 3 MB 超の本体（ストリーム読み取り）と、0 バイトを含む短い本体（末尾が欠けないこと）を入れる
        var attachment1 = Bytes((3 * 1024 * 1024) + 17, 1);
        var image = Bytes(5000, 2);
        var regular = Bytes(300, 3);
        var thumbnail = Bytes(100, 4);
        var icon = Bytes(20, 5);
        var logo = Bytes(400, 6);
        var attachment2 = new byte[] { 0, 1, 2, 0, 0 };
        var rows = new List<SeedRow>
        {
            new(1, 10, Guid1, "Attachments", attachment1),
            new(2, 11, Guid2, "Images", image),
            new(3, 321, null, "SiteImage", regular, thumbnail, icon),
            new(4, 7, null, "TenantImage", logo),
            new(5, 12, Guid3, "Attachments", attachment2),
            // 対象外: Bin が NULL の添付、一時ファイル
            new(6, 13, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "Attachments", null),
            new(90, 14, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Temporary", Bytes(64, 9)),
        };
        var expected = new Dictionary<string, byte[]>
        {
            ["Attachments/" + Guid1] = attachment1,
            ["Images/" + Guid2] = image,
            ["SiteImage/321_Regular.png"] = regular,
            ["SiteImage/321_Thumbnail.png"] = thumbnail,
            ["SiteImage/321_Icon.png"] = icon,
            ["TenantImage/7_Logo.png"] = logo,
            ["Attachments/" + Guid3] = attachment2,
        };
        return new SeedData(rows, expected);
    }

    private static async Task<RdsConnection?> PrepareAsync(RdsDbms dbms)
    {
        var text = ConnectionStringOf(dbms);
        if (text is null)
        {
            return null;
        }
        await ExecuteAsync(dbms, text, "DROP TABLE IF EXISTS " + RdsSql.Quote(dbms, "Binaries"));
        await ExecuteAsync(dbms, text, CreateTable(dbms));
        return new RdsConnection(dbms, text);
    }

    // Pleasanter の Binaries 表のうち、このツールが使う列だけを持つ
    private static string CreateTable(RdsDbms dbms) => dbms switch
    {
        RdsDbms.SqlServer =>
            "CREATE TABLE [Binaries] ([BinaryId] bigint NOT NULL PRIMARY KEY, [ReferenceId] bigint NOT NULL, [Guid] varchar(32) NULL, "
            + "[BinaryType] varchar(64) NULL, [ContentType] nvarchar(256) NULL, "
            + "[Bin] varbinary(max) NULL, [Thumbnail] varbinary(max) NULL, [Icon] varbinary(max) NULL)",
        RdsDbms.PostgreSql =>
            "CREATE TABLE \"Binaries\" (\"BinaryId\" bigint NOT NULL PRIMARY KEY, \"ReferenceId\" bigint NOT NULL, \"Guid\" varchar(32) NULL, "
            + "\"BinaryType\" varchar(64) NULL, \"ContentType\" varchar(256) NULL, "
            + "\"Bin\" bytea NULL, \"Thumbnail\" bytea NULL, \"Icon\" bytea NULL)",
        RdsDbms.MySql =>
            "CREATE TABLE `Binaries` (`BinaryId` bigint NOT NULL PRIMARY KEY, `ReferenceId` bigint NOT NULL, `Guid` varchar(32) NULL, "
            + "`BinaryType` varchar(64) NULL, `ContentType` varchar(256) NULL, "
            + "`Bin` longblob NULL, `Thumbnail` longblob NULL, `Icon` longblob NULL)",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    private static DbConnection Open(RdsDbms dbms, string text) => dbms switch
    {
        RdsDbms.SqlServer => new SqlConnection(text),
        RdsDbms.PostgreSql => new NpgsqlConnection(text),
        RdsDbms.MySql => new MySqlConnection(text),
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    private static async Task ExecuteAsync(RdsDbms dbms, string text, string sql)
    {
        await using var db = Open(dbms, text);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertAllAsync(RdsDbms dbms, string text, SeedData data)
    {
        await using var db = Open(dbms, text);
        await db.OpenAsync();
        foreach (var row in data.Rows)
        {
            await using var command = db.CreateCommand();
            var q = (string name) => RdsSql.Quote(dbms, name);
            command.CommandText = $"INSERT INTO {q("Binaries")} ({q("BinaryId")}, {q("ReferenceId")}, {q("Guid")}, {q("BinaryType")}, {q("Bin")}, {q("Thumbnail")}, {q("Icon")}) "
                + "VALUES (@id, @ref, @guid, @type, @bin, @thumb, @icon)";
            AddLong(command, "@id", row.BinaryId);
            AddLong(command, "@ref", row.ReferenceId);
            AddText(command, "@guid", row.Guid);
            AddText(command, "@type", row.BinaryType);
            AddBytes(command, "@bin", row.Bin);
            AddBytes(command, "@thumb", row.Thumbnail);
            AddBytes(command, "@icon", row.Icon);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<byte[]?> ReadColumnAsync(RdsDbms dbms, string text, long binaryId, string column)
    {
        await using var db = Open(dbms, text);
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = $"SELECT {RdsSql.Quote(dbms, column)} FROM {RdsSql.Quote(dbms, "Binaries")} WHERE {RdsSql.Quote(dbms, "BinaryId")} = @id";
        AddLong(command, "@id", binaryId);
        var value = await command.ExecuteScalarAsync();
        return value as byte[];
    }

    private static void AddLong(DbCommand command, string name, long value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int64;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void AddText(DbCommand command, string name, string? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static void AddBytes(DbCommand command, string name, byte[]? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Binary;
        parameter.Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
