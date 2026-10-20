using System.Text;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>Rds（Pleasanter の DB）の種類。</summary>
public enum RdsDbms
{
    SqlServer,
    PostgreSql,
    MySql,
}

/// <summary><c>Binaries</c> 表の、本体を持ち得る列。</summary>
public enum RdsColumn
{
    Bin,
    Thumbnail,
    Icon,
}

public static class RdsDbmsNames
{
    /// <summary>「sqlserver」「postgresql」「mysql」（別名を含む）を解く。</summary>
    public static bool TryParse(string? text, out RdsDbms dbms)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "sqlserver" or "mssql" or "sql-server":
                dbms = RdsDbms.SqlServer;
                return true;
            case "postgresql" or "postgres" or "npgsql":
                dbms = RdsDbms.PostgreSql;
                return true;
            case "mysql":
                dbms = RdsDbms.MySql;
                return true;
            default:
                dbms = default;
                return false;
        }
    }

    public static string ToDisplayName(RdsDbms dbms) => dbms switch
    {
        RdsDbms.SqlServer => "SQL Server",
        RdsDbms.PostgreSql => "PostgreSQL",
        RdsDbms.MySql => "MySQL",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };
}

/// <summary>
/// Rds へ発行する SQL の組み立て。DBMS ごとに引用符・件数の絞り・長さの関数が違う。
/// 値はすべてパラメーター（<c>@last</c>、<c>@id</c>、<c>@size</c>）で渡す。列名・表名・種別は、ここで列挙したコードの定数だけを使い、
/// 利用者の入力を SQL へ連結しない。読み取り用の SQL は SELECT だけで、更新は <see cref="ClearColumn"/> の 1 つだけ。
/// </summary>
internal static class RdsSql
{
    public const string TableName = "Binaries";

    public static string Quote(RdsDbms dbms, string identifier) => dbms switch
    {
        RdsDbms.SqlServer => "[" + identifier + "]",
        RdsDbms.PostgreSql => "\"" + identifier + "\"",
        RdsDbms.MySql => "`" + identifier + "`",
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };

    public static string ColumnName(RdsColumn column) => column switch
    {
        RdsColumn.Bin => "Bin",
        RdsColumn.Thumbnail => "Thumbnail",
        RdsColumn.Icon => "Icon",
        _ => throw new ArgumentOutOfRangeException(nameof(column)),
    };

    /// <summary>列の中身の長さ（バイト）を返す式。</summary>
    public static string LengthExpression(RdsDbms dbms, RdsColumn column)
    {
        var quoted = Quote(dbms, ColumnName(column));
        return dbms switch
        {
            RdsDbms.SqlServer => $"DATALENGTH({quoted})",
            RdsDbms.PostgreSql => $"octet_length({quoted})",
            RdsDbms.MySql => $"LENGTH({quoted})",
            _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
        };
    }

    /// <summary>
    /// 移行対象の行の条件。<c>Bin</c>（サイト画像は <c>Thumbnail</c>・<c>Icon</c> も）が NULL でないものだけ。
    /// NULL の行は、すでに外部ストレージにある（または本体が無い）ため対象にしない。
    /// </summary>
    public static string WhereCondition(RdsDbms dbms, IEnumerable<BinaryGroup> groups)
    {
        var type = Quote(dbms, "BinaryType");
        var bin = Quote(dbms, "Bin");
        var thumbnail = Quote(dbms, "Thumbnail");
        var icon = Quote(dbms, "Icon");
        var parts = new List<string>();
        foreach (var group in groups.Distinct())
        {
            parts.Add(group switch
            {
                BinaryGroup.Attachments => $"({type} = 'Attachments' AND {bin} IS NOT NULL)",
                BinaryGroup.Images => $"({type} IN ('Images', 'TenantManagementImages') AND {bin} IS NOT NULL)",
                BinaryGroup.SiteImage =>
                    $"({type} = 'SiteImage' AND ({bin} IS NOT NULL OR {thumbnail} IS NOT NULL OR {icon} IS NOT NULL))",
                BinaryGroup.TenantImage => $"({type} = 'TenantImage' AND {bin} IS NOT NULL)",
                _ => throw new ArgumentOutOfRangeException(nameof(groups)),
            });
        }
        if (parts.Count == 0)
        {
            return "1 = 0";
        }
        return "(" + string.Join(" OR ", parts) + ")";
    }

    /// <summary>
    /// 1 ページ分の一覧。<c>BinaryId</c> の昇順で、<c>@last</c> より大きいものを <paramref name="pageSize"/> 件まで（キーセットの頁送り）。
    /// 本体（<c>Bin</c> など）は読まず、長さだけを取る。
    /// </summary>
    public static string PageQuery(RdsDbms dbms, IReadOnlyCollection<BinaryGroup> groups, int pageSize)
    {
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        var q = (string name) => Quote(dbms, name);
        var includeImages = groups.Contains(BinaryGroup.SiteImage);
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        if (dbms == RdsDbms.SqlServer)
        {
            sb.Append("TOP (").Append(pageSize).Append(") ");
        }
        sb.Append(q("BinaryId")).Append(", ")
          .Append(q("ReferenceId")).Append(", ")
          .Append(q("Guid")).Append(", ")
          .Append(q("BinaryType")).Append(", ")
          .Append(q("ContentType")).Append(", ")
          .Append(LengthExpression(dbms, RdsColumn.Bin)).Append(" AS ").Append(q("BinSize")).Append(", ")
          .Append(includeImages ? LengthExpression(dbms, RdsColumn.Thumbnail) : "NULL").Append(" AS ").Append(q("ThumbnailSize")).Append(", ")
          .Append(includeImages ? LengthExpression(dbms, RdsColumn.Icon) : "NULL").Append(" AS ").Append(q("IconSize"));
        sb.Append(" FROM ").Append(q(TableName));
        sb.Append(" WHERE ").Append(q("BinaryId")).Append(" > @last AND ").Append(WhereCondition(dbms, groups));
        sb.Append(" ORDER BY ").Append(q("BinaryId"));
        if (dbms != RdsDbms.SqlServer)
        {
            sb.Append(" LIMIT ").Append(pageSize);
        }
        return sb.ToString();
    }

    /// <summary>1 行の 1 列の本体を読む（<c>@id</c> = <c>BinaryId</c>）。</summary>
    public static string ReadColumn(RdsDbms dbms, RdsColumn column)
        => $"SELECT {Quote(dbms, ColumnName(column))} FROM {Quote(dbms, TableName)} WHERE {Quote(dbms, "BinaryId")} = @id";

    /// <summary>
    /// 移行元の削除（<c>--delete-source</c>）。その列を NULL にする。<c>@id</c> と、読んだときの長さ <c>@size</c> が一致する行だけが対象。
    /// 読んだあとに行が変わっていれば 0 行が更新される。
    /// </summary>
    public static string ClearColumn(RdsDbms dbms, RdsColumn column)
    {
        var name = Quote(dbms, ColumnName(column));
        return $"UPDATE {Quote(dbms, TableName)} SET {name} = NULL "
            + $"WHERE {Quote(dbms, "BinaryId")} = @id AND {name} IS NOT NULL AND {LengthExpression(dbms, column)} = @size";
    }

    /// <summary>
    /// 読み取り用の接続を開いた直後に実行する、読み取り専用の指定。SQL Server にはセッション単位の指定が無いため、
    /// 接続文字列の <c>ApplicationIntent=ReadOnly</c> と、読み取りだけの SQL に頼る。
    /// </summary>
    public static IReadOnlyList<string> ReadOnlySessionStatements(RdsDbms dbms) => dbms switch
    {
        RdsDbms.SqlServer => [],
        RdsDbms.PostgreSql => ["SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY"],
        RdsDbms.MySql => ["SET SESSION TRANSACTION READ ONLY"],
        _ => throw new ArgumentOutOfRangeException(nameof(dbms)),
    };
}

/// <summary><c>Binaries</c> の 1 行（一覧の結果）。</summary>
internal sealed record RdsRow(
    long BinaryId,
    long ReferenceId,
    string? Guid,
    string? BinaryType,
    string? ContentType,
    long? BinSize,
    long? ThumbnailSize,
    long? IconSize);

/// <summary>
/// <c>Binaries</c> の行から、移行先のオブジェクト名と元の列を決める。対応は事実確認の結果による
/// （_documents/アーキテクチャ方針.md）。
/// </summary>
internal static class RdsRowMapper
{
    private const string PngContentType = "image/png";

    public static string KeyOf(long binaryId, RdsColumn column) => $"{binaryId}/{RdsSql.ColumnName(column)}";

    public static bool TryParseKey(string key, out long binaryId, out RdsColumn column)
    {
        binaryId = 0;
        column = default;
        var slash = key.IndexOf('/');
        return slash > 0
            && long.TryParse(key.AsSpan(0, slash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out binaryId)
            && Enum.TryParse(key[(slash + 1)..], ignoreCase: false, out column)
            && Enum.IsDefined(column);
    }

    /// <summary>1 行から、移行する対象（0 件以上）を作る。対象外の種別（一時ファイル・エクスポート）は何も返さない。</summary>
    public static IEnumerable<SourceObject> Map(RdsRow row, ISet<BinaryGroup> groups)
    {
        var contentType = string.IsNullOrWhiteSpace(row.ContentType) ? null : row.ContentType;
        switch (row.BinaryType)
        {
            case "Attachments" when groups.Contains(BinaryGroup.Attachments) && row.BinSize is not null:
                yield return WithGuid(row, RdsColumn.Bin, row.BinSize.Value, ObjectNames.Attachment, contentType);
                break;

            // テナント管理の画像は、Pleasanter の読み出しで Images として扱われる（同じオブジェクト名）
            case "Images" or "TenantManagementImages" when groups.Contains(BinaryGroup.Images) && row.BinSize is not null:
                yield return WithGuid(row, RdsColumn.Bin, row.BinSize.Value, ObjectNames.Image, contentType);
                break;

            case "SiteImage" when groups.Contains(BinaryGroup.SiteImage):
                if (row.BinSize is not null)
                {
                    yield return Item(row, RdsColumn.Bin, ObjectNames.SiteImage(row.ReferenceId, SiteImageSize.Regular), row.BinSize.Value);
                }
                if (row.ThumbnailSize is not null)
                {
                    yield return Item(row, RdsColumn.Thumbnail, ObjectNames.SiteImage(row.ReferenceId, SiteImageSize.Thumbnail), row.ThumbnailSize.Value);
                }
                if (row.IconSize is not null)
                {
                    yield return Item(row, RdsColumn.Icon, ObjectNames.SiteImage(row.ReferenceId, SiteImageSize.Icon), row.IconSize.Value);
                }
                break;

            case "TenantImage" when groups.Contains(BinaryGroup.TenantImage) && row.BinSize is not null:
                yield return Item(row, RdsColumn.Bin, ObjectNames.TenantImageLogo(row.ReferenceId), row.BinSize.Value);
                break;
        }
    }

    private static SourceObject Item(RdsRow row, RdsColumn column, string objectName, long size)
        => new(objectName, size, KeyOf(row.BinaryId, column), PngContentType);

    private static SourceObject WithGuid(RdsRow row, RdsColumn column, long size, Func<string, string> name, string? contentType)
    {
        if (!ObjectNames.IsValidGuid(row.Guid))
        {
            // 名前を作れない行。コピーせず、失敗として数える
            return new SourceObject(
                $"Binaries/BinaryId={row.BinaryId}",
                size,
                KeyOf(row.BinaryId, column),
                contentType,
                Problem: "The Guid of the Binaries row is not 32 hexadecimal digits, so no object name can be made.");
        }
        return new SourceObject(name(row.Guid!), size, KeyOf(row.BinaryId, column), contentType);
    }
}
