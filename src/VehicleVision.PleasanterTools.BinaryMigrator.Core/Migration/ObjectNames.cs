using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>移行の対象にする種別のまとまり。Pleasanter の <c>BinaryType</c> と外部ストレージの先頭フォルダに対応する。</summary>
public enum BinaryGroup
{
    /// <summary>添付ファイル（<c>Attachments/{Guid}</c>）。</summary>
    Attachments,

    /// <summary>画像（<c>Images/{Guid}</c> と <c>Images/{Guid}_thumbnail</c>。テナント管理の画像も含む）。</summary>
    Images,

    /// <summary>サイト画像（<c>SiteImage/{サイトID}_{Regular|Thumbnail|Icon}.png</c>）。</summary>
    SiteImage,

    /// <summary>テナント画像（<c>TenantImage/{テナントの ReferenceId}_Logo.png</c>）。</summary>
    TenantImage,
}

/// <summary>サイト画像の大きさの種別（オブジェクト名の末尾）。</summary>
public enum SiteImageSize
{
    Regular,
    Thumbnail,
    Icon,
}

/// <summary>
/// Pleasanter が外部ストレージ（Local・AzureBlob）で使うオブジェクト名の組み立てと検証。
/// 規則は事実確認の結果（_documents/アーキテクチャ方針.md）による。
/// </summary>
public static class ObjectNames
{
    private const int MaxLength = 1024;

    private static readonly Regex GuidPattern = new("^[0-9A-Fa-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 名前の 1 区間に使えない文字。<b>OS によらず同じ判定にする</b>（Windows で無効な文字を、Linux でも拒む。
    /// 移行先のフォルダを別の OS へ持っていっても、名前が壊れないようにするため）。OS が無効とする文字も足す。
    /// </summary>
    private static readonly char[] InvalidSegmentChars =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>種別のまとまりに対応する、オブジェクト名の先頭（末尾の <c>/</c> を含む）。</summary>
    public static IReadOnlyList<string> Prefixes(BinaryGroup group) => group switch
    {
        BinaryGroup.Attachments => ["Attachments/"],
        BinaryGroup.Images => ["Images/"],
        BinaryGroup.SiteImage => ["SiteImage/"],
        BinaryGroup.TenantImage => ["TenantImage/"],
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>Pleasanter の添付ファイルの GUID の形（ハイフンなし 32 桁の 16 進）か。</summary>
    public static bool IsValidGuid(string? guid) => guid is not null && GuidPattern.IsMatch(guid);

    public static string Attachment(string guid) => "Attachments/" + RequireGuid(guid);

    public static string Image(string guid) => "Images/" + RequireGuid(guid);

    public static string ImageThumbnail(string guid) => "Images/" + RequireGuid(guid) + "_thumbnail";

    public static string SiteImage(long referenceId, SiteImageSize size) => $"SiteImage/{referenceId}_{size}.png";

    public static string TenantImageLogo(long referenceId) => $"TenantImage/{referenceId}_Logo.png";

    private static string RequireGuid(string guid)
        => IsValidGuid(guid) ? guid : throw new ArgumentException("The GUID is not 32 hexadecimal digits.", nameof(guid));

    /// <summary>
    /// オブジェクト名が安全か。<c>/</c> 区切りの相対名だけを許し、
    /// 絶対パス・ドライブ名・UNC・<c>..</c>・制御文字・Windows の予約名などを拒む（パストラバーサルの禁止）。
    /// </summary>
    /// <returns>問題が無ければ null、あれば理由（英語）。</returns>
    public static string? FindProblem(string? objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName))
        {
            return "The object name is empty.";
        }
        if (objectName.Length > MaxLength)
        {
            return "The object name is too long.";
        }
        if (objectName.Contains("..", StringComparison.Ordinal))
        {
            return "Path traversal is not allowed.";
        }
        if (objectName.Contains('\\'))
        {
            return "Backslashes are not allowed in object names.";
        }
        if (objectName.Contains(':'))
        {
            return "Drive letters and stream names are not allowed.";
        }
        if (objectName.StartsWith('/'))
        {
            return "Absolute paths are not allowed.";
        }
        foreach (var ch in objectName)
        {
            if (char.IsControl(ch))
            {
                return "Control characters are not allowed.";
            }
        }
        var invalidChars = InvalidSegmentChars;
        foreach (var segment in objectName.Split('/'))
        {
            if (segment.Length == 0)
            {
                return "Empty path segments are not allowed.";
            }
            if (segment is "." or "..")
            {
                return "Path traversal is not allowed.";
            }
            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return "Segments must not end with a dot or a space.";
            }
            if (segment.IndexOfAny(invalidChars) >= 0)
            {
                return "The object name contains characters that are not allowed in file names.";
            }
            var stem = segment.Split('.')[0];
            if (ReservedDeviceNames.Contains(stem))
            {
                return "Reserved device names are not allowed.";
            }
        }
        return null;
    }

    /// <summary>安全でなければ <see cref="ArgumentException"/>。</summary>
    public static void Validate(string? objectName)
    {
        var problem = FindProblem(objectName);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(objectName));
        }
    }

    /// <summary>
    /// オブジェクト名を、保管先のルートの下の相対パス（OS の区切り）へ写す。検証を通らなければ例外。
    /// </summary>
    public static string ToRelativePath(string objectName)
    {
        Validate(objectName);
        return objectName.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>相対パス（OS の区切り）を、オブジェクト名へ戻す。</summary>
    public static string FromRelativePath(string relativePath)
        => relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}

/// <summary><c>--types</c> の解析。</summary>
public static class BinaryGroups
{
    public static IReadOnlyList<BinaryGroup> All { get; } =
        [BinaryGroup.Attachments, BinaryGroup.Images, BinaryGroup.SiteImage, BinaryGroup.TenantImage];

    /// <summary>「attachments,images」のようなコンマ区切りを解く（大文字小文字を問わない）。</summary>
    public static bool TryParse(string? text, out IReadOnlyList<BinaryGroup> groups, out string? error)
    {
        groups = All;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "--types needs a comma-separated list: attachments,images,siteimage,tenantimage.";
            return false;
        }
        var result = new List<BinaryGroup>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // 数値（「1」など）を別名として受けない
            if (char.IsAsciiDigit(part[0]) || !Enum.TryParse<BinaryGroup>(part, ignoreCase: true, out var group) || !Enum.IsDefined(group))
            {
                error = $"Unknown type '{part}'. Use attachments, images, siteimage or tenantimage.";
                return false;
            }
            if (!result.Contains(group))
            {
                result.Add(group);
            }
        }
        if (result.Count == 0)
        {
            error = "--types needs at least one type.";
            return false;
        }
        groups = result;
        return true;
    }
}
