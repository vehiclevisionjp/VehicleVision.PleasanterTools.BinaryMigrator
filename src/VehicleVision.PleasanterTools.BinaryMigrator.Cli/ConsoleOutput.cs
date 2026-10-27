using VehicleVision.PleasanterTools.BinaryMigrator.Core;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Cli;

/// <summary>標準出力への書き込み。複数の処理から呼ばれるため直列にし、秘密を伏せ、ASCII 以外を <c>?</c> にする。</summary>
internal sealed class ConsoleOutput
{
    private readonly SecretRedactor _redactor;
    private readonly object _gate = new();

    public ConsoleOutput(SecretRedactor redactor)
    {
        _redactor = redactor;
    }

    public void Line(string text)
    {
        var safe = ReportFormatter.ToAscii(_redactor.Redact(text));
        lock (_gate)
        {
            Console.Out.WriteLine(safe);
        }
    }
}

/// <summary>進み具合をコンソールへ出す。<c>--quiet</c> のときは、成功した件の行を出さない。</summary>
internal sealed class ConsoleReporter : IMigrationReporter
{
    private readonly ConsoleOutput _output;
    private readonly bool _quiet;

    public ConsoleReporter(ConsoleOutput output, bool quiet)
    {
        _output = output;
        _quiet = quiet;
    }

    public void ItemCompleted(long sequence, ItemResult result)
    {
        if (_quiet && result.Outcome != ItemOutcome.Failed)
        {
            return;
        }
        _output.Line(ReportFormatter.FormatItem(sequence, result));
    }

    public void Retrying(string objectName, int attempt, int maxRetries, string reason)
        => _output.Line(ReportFormatter.FormatRetry(objectName, attempt, maxRetries, reason));
}
