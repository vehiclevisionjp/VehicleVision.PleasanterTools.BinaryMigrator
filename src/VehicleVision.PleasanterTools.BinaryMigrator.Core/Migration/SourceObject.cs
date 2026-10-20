namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// 移行元から列挙した 1 つのオブジェクト（移行先へ写す 1 件）。
/// </summary>
/// <param name="ObjectName">移行先でのオブジェクト名（例: <c>Attachments/0123...</c>）。<c>/</c> 区切り。</param>
/// <param name="Size">移行元で報告された大きさ（バイト）。コピーした大きさと照合する。</param>
/// <param name="Key">移行元の中での識別子。Rds では「BinaryId/列名」、Local・AzureBlob ではオブジェクト名と同じ。</param>
/// <param name="ContentType">分かっていれば移行先へ付けるコンテンツタイプ。</param>
/// <param name="Problem">移行元の記録に問題があり、移行できないときの理由（英語。コンソールへ出す）。値があれば、コピーせず失敗として数える。</param>
public sealed record SourceObject(
    string ObjectName,
    long Size,
    string Key,
    string? ContentType = null,
    string? Problem = null);
