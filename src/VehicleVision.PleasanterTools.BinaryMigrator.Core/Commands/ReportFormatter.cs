using System.Globalization;
using System.Text;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// コンソールへ出す進み具合と結果の文字列。<b>すべて英語・ASCII</b>
/// （Azure の Kudu の Debug console では日本語が化けるため。ファイル名など ASCII 以外は <c>?</c> にする）。
/// </summary>
public static class ReportFormatter
{
    public static string FormatItem(long sequence, ItemResult result)
    {
        var label = result.Outcome switch
        {
            ItemOutcome.Copied => "COPIED   ",
            ItemOutcome.Skipped => "SKIPPED  ",
            ItemOutcome.Failed => "FAILED   ",
            ItemOutcome.PlannedCopy => "WOULD-COPY",
            ItemOutcome.PlannedSkip => "WOULD-SKIP",
            _ => "?        ",
        };
        var sb = new StringBuilder();
        sb.Append('[').Append(sequence.ToString(CultureInfo.InvariantCulture)).Append("] ")
          .Append(label.TrimEnd()).Append(' ')
          .Append(ToAscii(result.Item.ObjectName))
          .Append(" (").Append(FormatSize(result.Item.Size)).Append(')');
        if (result.Outcome == ItemOutcome.Skipped)
        {
            sb.Append(" same size and SHA-256 already at the destination");
        }
        if (result.Outcome == ItemOutcome.PlannedSkip)
        {
            sb.Append(" same size at the destination (SHA-256 is not checked in a dry run)");
        }
        if (result.SourceDeleted)
        {
            sb.Append(" source-deleted");
        }
        if (result.WouldDeleteSource)
        {
            sb.Append(" would-delete-source");
        }
        if (result.Error is not null)
        {
            sb.Append(": ").Append(ToAscii(result.Error));
        }
        return sb.ToString();
    }

    public static string FormatRetry(string objectName, int attempt, int maxRetries, string reason)
        => $"  retry {attempt}/{maxRetries} {ToAscii(objectName)}: {ToAscii(reason)}";

    /// <summary>最後にまとめて出す結果（件数・失敗の一覧・とばした数）。</summary>
    public static IReadOnlyList<string> FormatSummary(MigrationReport report)
    {
        var lines = new List<string> { string.Empty, report.DryRun ? "=== Dry run summary (nothing was written or deleted) ===" : "=== Summary ===" };
        lines.Add($"Objects examined : {report.Total}");
        if (report.DryRun)
        {
            lines.Add($"Would copy       : {report.PlannedCopy}");
            lines.Add($"Would skip       : {report.PlannedSkip} (same size already at the destination)");
            if (report.DeleteSource)
            {
                lines.Add($"Would delete src : {report.PlannedCopy + report.PlannedSkip} (only after each copy is verified)");
            }
        }
        else
        {
            lines.Add($"Copied           : {report.Copied} ({FormatSize(report.BytesCopied)})");
            lines.Add($"Skipped          : {report.Skipped} (same size and SHA-256 already at the destination)");
            if (report.DeleteSource)
            {
                lines.Add($"Source deleted   : {report.SourceDeleted}");
            }
        }
        lines.Add($"Failed           : {report.Failed}");

        var failures = report.Failures;
        if (failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Failures (the source was NOT deleted for these):");
            foreach (var failure in failures)
            {
                lines.Add("  " + ToAscii(failure.Item.ObjectName) + ": " + ToAscii(failure.Error ?? "unknown error"));
            }
        }

        if (report.Aborted)
        {
            lines.Add(string.Empty);
            lines.Add("STOPPED: " + ToAscii(report.AbortReason ?? "unknown reason") + " (the remaining objects were not processed)");
        }

        lines.Add(string.Empty);
        lines.Add(report.Succeeded ? "Result: success." : "Result: NOT all succeeded. Fix the problems above and run again (finished items are skipped).");
        if (!report.DryRun)
        {
            lines.Add("Note: Pleasanter's BinaryStorage.json was not modified. Switch its Provider yourself after you have verified the destination.");
        }
        return lines;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    /// <summary>ASCII 以外と制御文字を <c>?</c> にし、1 行にする。</summary>
    public static string ToAscii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch is >= ' ' and <= '~' ? ch : ch is '\r' or '\n' or '\t' ? ' ' : '?');
        }
        return sb.ToString();
    }
}
