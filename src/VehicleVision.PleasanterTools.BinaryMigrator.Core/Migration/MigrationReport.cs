namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>1 件の結果の種類。</summary>
public enum ItemOutcome
{
    /// <summary>コピーして、移行先で大きさとハッシュを確かめた。</summary>
    Copied,

    /// <summary>移行先に同じ大きさ・同じハッシュのものがあったため、とばした。</summary>
    Skipped,

    /// <summary>失敗した（コピーできない・検証できない・移行元の削除に失敗した）。</summary>
    Failed,

    /// <summary>ドライラン: コピーする予定。</summary>
    PlannedCopy,

    /// <summary>ドライラン: 移行先に同じ大きさのものがあるため、とばす見込み（ハッシュはドライランでは確かめない）。</summary>
    PlannedSkip,
}

/// <summary>1 件の結果。</summary>
/// <param name="Item">対象。</param>
/// <param name="Outcome">結果の種類。</param>
/// <param name="SourceDeleted">移行元を削除した（ドライランでは常に false）。</param>
/// <param name="WouldDeleteSource">ドライランで、実行すれば移行元を削除する見込み。</param>
/// <param name="Error">失敗の理由（英語）。</param>
public sealed record ItemResult(
    SourceObject Item,
    ItemOutcome Outcome,
    bool SourceDeleted = false,
    bool WouldDeleteSource = false,
    string? Error = null);

/// <summary>移行の結果の集計。複数の処理から同時に更新される。</summary>
public sealed class MigrationReport
{
    private readonly object _gate = new();
    private readonly List<ItemResult> _failures = [];
    private long _total;
    private long _copied;
    private long _skipped;
    private long _failed;
    private long _plannedCopy;
    private long _plannedSkip;
    private long _sourceDeleted;
    private long _bytesCopied;

    public MigrationReport(bool dryRun, bool deleteSource)
    {
        DryRun = dryRun;
        DeleteSource = deleteSource;
    }

    public bool DryRun { get; }

    public bool DeleteSource { get; }

    public long Total => Interlocked.Read(ref _total);
    public long Copied => Interlocked.Read(ref _copied);
    public long Skipped => Interlocked.Read(ref _skipped);
    public long Failed => Interlocked.Read(ref _failed);
    public long PlannedCopy => Interlocked.Read(ref _plannedCopy);
    public long PlannedSkip => Interlocked.Read(ref _plannedSkip);
    public long SourceDeleted => Interlocked.Read(ref _sourceDeleted);
    public long BytesCopied => Interlocked.Read(ref _bytesCopied);

    /// <summary>全件に効く失敗や一覧の取得失敗で、途中で止めたか。</summary>
    public bool Aborted { get; private set; }

    /// <summary>止めた理由（英語）。</summary>
    public string? AbortReason { get; private set; }

    /// <summary>失敗の一覧（オブジェクト名順）。</summary>
    public IReadOnlyList<ItemResult> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.OrderBy(f => f.Item.ObjectName, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <summary>全件成功（失敗なし・中断なし）か。終了コード 0 の条件。</summary>
    public bool Succeeded => Failed == 0 && !Aborted;

    internal void Add(ItemResult result)
    {
        Interlocked.Increment(ref _total);
        switch (result.Outcome)
        {
            case ItemOutcome.Copied:
                Interlocked.Increment(ref _copied);
                Interlocked.Add(ref _bytesCopied, result.Item.Size);
                break;
            case ItemOutcome.Skipped:
                Interlocked.Increment(ref _skipped);
                break;
            case ItemOutcome.Failed:
                Interlocked.Increment(ref _failed);
                lock (_gate)
                {
                    _failures.Add(result);
                }
                break;
            case ItemOutcome.PlannedCopy:
                Interlocked.Increment(ref _plannedCopy);
                break;
            case ItemOutcome.PlannedSkip:
                Interlocked.Increment(ref _plannedSkip);
                break;
        }
        if (result.SourceDeleted)
        {
            Interlocked.Increment(ref _sourceDeleted);
        }
    }

    internal void Abort(string reason)
    {
        lock (_gate)
        {
            Aborted = true;
            AbortReason ??= reason;
        }
    }
}

/// <summary>進み具合の通知先。実装は Cli（コンソール）。通知の文字列は英語。</summary>
public interface IMigrationReporter
{
    /// <summary>1 件が終わった。<paramref name="sequence"/> は 1 から始まる通し番号。</summary>
    void ItemCompleted(long sequence, ItemResult result);

    /// <summary>一時的な失敗のため、再試行する。</summary>
    void Retrying(string objectName, int attempt, int maxRetries, string reason);
}

/// <summary>何も出さない通知先（テスト用）。</summary>
public sealed class NullMigrationReporter : IMigrationReporter
{
    public static NullMigrationReporter Instance { get; } = new();

    public void ItemCompleted(long sequence, ItemResult result) { }

    public void Retrying(string objectName, int attempt, int maxRetries, string reason) { }
}
