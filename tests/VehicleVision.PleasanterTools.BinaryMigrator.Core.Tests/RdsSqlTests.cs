namespace VehicleVision.PleasanterTools.BinaryMigrator.Core.Tests;

/// <summary>DB へ接続せずに試せる、SQL の組み立てと行の対応。実機の DB での確認は別（ドキュメント参照）。</summary>
public sealed class RdsSqlTests
{
    private const string Guid32 = "0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("sqlserver", RdsDbms.SqlServer)]
    [InlineData("SQLServer", RdsDbms.SqlServer)]
    [InlineData("mssql", RdsDbms.SqlServer)]
    [InlineData("postgresql", RdsDbms.PostgreSql)]
    [InlineData("postgres", RdsDbms.PostgreSql)]
    [InlineData("mysql", RdsDbms.MySql)]
    public void DBMS名の解析(string text, RdsDbms expected)
    {
        Assert.True(RdsDbmsNames.TryParse(text, out var dbms));
        Assert.Equal(expected, dbms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("oracle")]
    [InlineData(null)]
    public void 未対応のDBMSは解析できない(string? text)
        => Assert.False(RdsDbmsNames.TryParse(text, out _));

    [Fact]
    public void 引用符は_DBMSごとに違う()
    {
        Assert.Equal("[Bin]", RdsSql.Quote(RdsDbms.SqlServer, "Bin"));
        Assert.Equal("\"Bin\"", RdsSql.Quote(RdsDbms.PostgreSql, "Bin"));
        Assert.Equal("`Bin`", RdsSql.Quote(RdsDbms.MySql, "Bin"));
    }

    [Theory]
    [InlineData(RdsDbms.SqlServer, "DATALENGTH([Bin])")]
    [InlineData(RdsDbms.PostgreSql, "octet_length(\"Bin\")")]
    [InlineData(RdsDbms.MySql, "LENGTH(`Bin`)")]
    public void 長さの式は_DBMSごとに違う(RdsDbms dbms, string expected)
        => Assert.Equal(expected, RdsSql.LengthExpression(dbms, RdsColumn.Bin));

    [Fact]
    public void SQLServer_の一覧は_TOP_と_キーセットの頁送り()
    {
        var sql = RdsSql.PageQuery(RdsDbms.SqlServer, BinaryGroups.All.ToList(), 200);

        Assert.StartsWith("SELECT TOP (200) [BinaryId]", sql);
        Assert.Contains("FROM [Binaries]", sql);
        Assert.Contains("[BinaryId] > @last", sql);
        Assert.EndsWith("ORDER BY [BinaryId]", sql);
        Assert.DoesNotContain("LIMIT", sql);
    }

    [Theory]
    [InlineData(RdsDbms.PostgreSql, "FROM \"Binaries\"", "ORDER BY \"BinaryId\" LIMIT 50")]
    [InlineData(RdsDbms.MySql, "FROM `Binaries`", "ORDER BY `BinaryId` LIMIT 50")]
    public void PostgreSQLとMySQLの一覧は_LIMIT(RdsDbms dbms, string from, string tail)
    {
        var sql = RdsSql.PageQuery(dbms, BinaryGroups.All.ToList(), 50);

        Assert.Contains(from, sql);
        Assert.EndsWith(tail, sql);
        Assert.DoesNotContain("TOP", sql);
    }

    [Fact]
    public void 一覧は_本体を読まず_長さだけを取る()
    {
        foreach (var dbms in Enum.GetValues<RdsDbms>())
        {
            var sql = RdsSql.PageQuery(dbms, BinaryGroups.All.ToList(), 10);
            var select = sql[..sql.IndexOf(" FROM ", StringComparison.Ordinal)];
            Assert.Contains(RdsSql.LengthExpression(dbms, RdsColumn.Bin), select);
            Assert.DoesNotContain(" " + RdsSql.Quote(dbms, "Bin") + ",", select); // 列そのものは選ばない
        }
    }

    [Fact]
    public void 対象の条件は_Bin_が_NULL_でない行だけ_種別ごと()
    {
        var all = RdsSql.WhereCondition(RdsDbms.SqlServer, BinaryGroups.All);
        Assert.Contains("[BinaryType] = 'Attachments' AND [Bin] IS NOT NULL", all);
        Assert.Contains("[BinaryType] IN ('Images', 'TenantManagementImages') AND [Bin] IS NOT NULL", all);
        Assert.Contains("[BinaryType] = 'SiteImage' AND ([Bin] IS NOT NULL OR [Thumbnail] IS NOT NULL OR [Icon] IS NOT NULL)", all);
        Assert.Contains("[BinaryType] = 'TenantImage' AND [Bin] IS NOT NULL", all);
        // 一時ファイル・エクスポートは対象外
        Assert.DoesNotContain("Temporary", all);
        Assert.DoesNotContain("ExportData", all);

        var onlyAttachments = RdsSql.WhereCondition(RdsDbms.PostgreSql, [BinaryGroup.Attachments]);
        Assert.Equal("((\"BinaryType\" = 'Attachments' AND \"Bin\" IS NOT NULL))", onlyAttachments);
        Assert.Equal("1 = 0", RdsSql.WhereCondition(RdsDbms.MySql, []));
    }

    [Fact]
    public void 値は連結せず_パラメーターで渡す()
    {
        foreach (var dbms in Enum.GetValues<RdsDbms>())
        {
            Assert.Contains("@last", RdsSql.PageQuery(dbms, BinaryGroups.All.ToList(), 5));
            Assert.Contains("@id", RdsSql.ReadColumn(dbms, RdsColumn.Bin));
            var clear = RdsSql.ClearColumn(dbms, RdsColumn.Bin);
            Assert.Contains("@id", clear);
            Assert.Contains("@size", clear);
        }
    }

    [Fact]
    public void 読み取りのSQLは_SELECTだけ_更新は_ClearColumn_だけ()
    {
        foreach (var dbms in Enum.GetValues<RdsDbms>())
        {
            foreach (var column in Enum.GetValues<RdsColumn>())
            {
                var read = RdsSql.ReadColumn(dbms, column);
                Assert.StartsWith("SELECT ", read);
                Assert.DoesNotContain("UPDATE", read, StringComparison.OrdinalIgnoreCase);
            }
            Assert.StartsWith("SELECT ", RdsSql.PageQuery(dbms, BinaryGroups.All.ToList(), 5));
            Assert.DoesNotContain("UPDATE", RdsSql.PageQuery(dbms, BinaryGroups.All.ToList(), 5), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 移行元の削除は_読んだときの長さが一致する行の_列だけをNULLにする()
    {
        Assert.Equal(
            "UPDATE [Binaries] SET [Bin] = NULL WHERE [BinaryId] = @id AND [Bin] IS NOT NULL AND DATALENGTH([Bin]) = @size",
            RdsSql.ClearColumn(RdsDbms.SqlServer, RdsColumn.Bin));
        Assert.Equal(
            "UPDATE \"Binaries\" SET \"Icon\" = NULL WHERE \"BinaryId\" = @id AND \"Icon\" IS NOT NULL AND octet_length(\"Icon\") = @size",
            RdsSql.ClearColumn(RdsDbms.PostgreSql, RdsColumn.Icon));
        Assert.Equal(
            "UPDATE `Binaries` SET `Thumbnail` = NULL WHERE `BinaryId` = @id AND `Thumbnail` IS NOT NULL AND LENGTH(`Thumbnail`) = @size",
            RdsSql.ClearColumn(RdsDbms.MySql, RdsColumn.Thumbnail));
    }

    [Fact]
    public void 読み取り専用のセッション指定()
    {
        Assert.Empty(RdsSql.ReadOnlySessionStatements(RdsDbms.SqlServer)); // 接続文字列の ApplicationIntent に頼る
        Assert.Equal(["SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY"], RdsSql.ReadOnlySessionStatements(RdsDbms.PostgreSql));
        Assert.Equal(["SET SESSION TRANSACTION READ ONLY"], RdsSql.ReadOnlySessionStatements(RdsDbms.MySql));
    }

    [Fact]
    public void 頁の大きさは1以上()
        => Assert.Throws<ArgumentOutOfRangeException>(() => RdsSql.PageQuery(RdsDbms.MySql, BinaryGroups.All.ToList(), 0));

    // ---- 行 → 移行対象 ----

    private static readonly HashSet<BinaryGroup> AllGroups = [.. BinaryGroups.All];

    [Fact]
    public void 添付は_Attachments_Guid_へ()
    {
        var row = new RdsRow(10, 99, Guid32, "Attachments", "application/pdf", 1234, null, null);

        var item = Assert.Single(RdsRowMapper.Map(row, AllGroups));

        Assert.Equal("Attachments/" + Guid32, item.ObjectName);
        Assert.Equal(1234, item.Size);
        Assert.Equal("10/Bin", item.Key);
        Assert.Equal("application/pdf", item.ContentType);
        Assert.Null(item.Problem);
    }

    [Theory]
    [InlineData("Images")]
    [InlineData("TenantManagementImages")] // Pleasanter は Images として読む
    public void 画像は_Images_Guid_へ_サムネイル列は触らない(string binaryType)
    {
        var row = new RdsRow(11, 5, Guid32, binaryType, "image/jpeg", 500, ThumbnailSize: 50, null);

        var item = Assert.Single(RdsRowMapper.Map(row, AllGroups));

        Assert.Equal("Images/" + Guid32, item.ObjectName);
        Assert.Equal("11/Bin", item.Key);
    }

    [Fact]
    public void サイト画像は_3つの大きさの列を_それぞれのオブジェクトへ()
    {
        var row = new RdsRow(12, 321, null, "SiteImage", "image/png", 300, 100, 20);

        var items = RdsRowMapper.Map(row, AllGroups).ToList();

        Assert.Equal(
            [("SiteImage/321_Regular.png", "12/Bin", 300L), ("SiteImage/321_Thumbnail.png", "12/Thumbnail", 100L), ("SiteImage/321_Icon.png", "12/Icon", 20L)],
            items.Select(i => (i.ObjectName, i.Key, i.Size)).ToArray());
        Assert.All(items, i => Assert.Equal("image/png", i.ContentType));
    }

    [Fact]
    public void サイト画像は_NULL_の列を飛ばす()
    {
        var row = new RdsRow(12, 321, null, "SiteImage", null, null, 100, null);
        var item = Assert.Single(RdsRowMapper.Map(row, AllGroups));
        Assert.Equal("SiteImage/321_Thumbnail.png", item.ObjectName);
    }

    [Fact]
    public void テナント画像は_ロゴ_へ()
    {
        var row = new RdsRow(13, 7, null, "TenantImage", null, 400, null, null);
        var item = Assert.Single(RdsRowMapper.Map(row, AllGroups));
        Assert.Equal("TenantImage/7_Logo.png", item.ObjectName);
    }

    [Theory]
    [InlineData("Temporary")]
    [InlineData("ExportData")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public void 対象外の種別は_何も返さない(string? binaryType)
        => Assert.Empty(RdsRowMapper.Map(new RdsRow(1, 1, Guid32, binaryType, null, 10, null, null), AllGroups));

    [Fact]
    public void 選ばれていない種別は_何も返さない()
        => Assert.Empty(RdsRowMapper.Map(
            new RdsRow(1, 1, Guid32, "Attachments", null, 10, null, null),
            new HashSet<BinaryGroup> { BinaryGroup.Images }));

    [Fact]
    public void Binが_NULL_の添付は_対象外()
        => Assert.Empty(RdsRowMapper.Map(new RdsRow(1, 1, Guid32, "Attachments", null, null, null, null), AllGroups));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("NOT-A-GUID")]
    public void Guidが不正な行は_名前を作らず_問題として返す(string? guid)
    {
        var item = Assert.Single(RdsRowMapper.Map(new RdsRow(77, 1, guid, "Attachments", null, 10, null, null), AllGroups));

        Assert.NotNull(item.Problem);
        Assert.Equal("Binaries/BinaryId=77", item.ObjectName); // 保管先の名前としては使わない
    }

    [Fact]
    public void キーは_往復で解析できる()
    {
        var key = RdsRowMapper.KeyOf(42, RdsColumn.Thumbnail);
        Assert.True(RdsRowMapper.TryParseKey(key, out var id, out var column));
        Assert.Equal(42, id);
        Assert.Equal(RdsColumn.Thumbnail, column);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]
    [InlineData("x/Bin")]
    [InlineData("42/Other")]
    [InlineData("-1/Bin")]
    [InlineData("42/99")]
    public void 不正なキーは解析できない(string key)
        => Assert.False(RdsRowMapper.TryParseKey(key, out _, out _));

    [Fact]
    public void Rdsの接続文字列が空なら_作れない()
        => Assert.Throws<ArgumentException>(() => new RdsBinarySource(new RdsConnection(RdsDbms.SqlServer, " ")));

    [Fact]
    public void 表示名には_接続先の秘密を含めない()
    {
        var source = new RdsBinarySource(new RdsConnection(RdsDbms.PostgreSql, "Host=db;Password=topsecret"));
        Assert.DoesNotContain("topsecret", source.DisplayName);
        Assert.Contains("PostgreSQL", source.DisplayName);
    }
}
