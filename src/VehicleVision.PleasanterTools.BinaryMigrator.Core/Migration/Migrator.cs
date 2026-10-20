namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>移行の実行条件。</summary>
public sealed record MigratorOptions
{
    /// <summary>何をどこへ移すかだけを調べ、何も書かない・消さない。</summary>
    public bool DryRun { get; init; }

    /// <summary>移行先で大きさとハッシュを確かめたものだけ、移行元を消す。既定では消さない。</summary>
    public bool DeleteSource { get; init; }

    /// <summary>同時に処理する件数の上限。既定は直列（1）。</summary>
    public int Parallel { get; init; } = 1;

    /// <summary>一時的な失敗（ネットワークなど）に対する再試行の回数。</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>再試行の待ち時間の基準（回を重ねるごとに倍。上限 30 秒）。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// 移行の本体。<see cref="IBinarySource"/> から <see cref="IBinarySink"/> へ写し、移行先を読み戻して大きさとハッシュを確かめる。
/// 抽象にだけ依存する。1 件の失敗では止めず、集計して返す。全件に効く失敗（<see cref="FatalMigrationException"/>）は即座に止める。
/// </summary>
public sealed class Migrator
{
    private readonly IBinarySource _source;
    private readonly IBinarySink _sink;
    private readonly MigratorOptions _options;
    private readonly IMigrationReporter _reporter;

    public Migrator(IBinarySource source, IBinarySink sink, MigratorOptions options, IMigrationReporter? reporter = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reporter = reporter ?? NullMigrationReporter.Instance;
        if (options.Parallel is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Parallel must be between 1 and 64.");
        }
        if (options.MaxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetries must not be negative.");
        }
        if (options.DeleteSource && source is not ISourceDeleter)
        {
            throw new ArgumentException("The source does not support deletion.", nameof(source));
        }
    }

    public async Task<MigrationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var report = new MigrationReport(_options.DryRun, _options.DeleteSource);

        try
        {
            await _source.CheckAsync(cancellationToken).ConfigureAwait(false);
            await _sink.CheckAsync(forWrite: !_options.DryRun, cancellationToken).ConfigureAwait(false);
        }
        catch (FatalMigrationException ex)
        {
            report.Abort(ex.Message);
            return report;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report.Abort("Interrupted.");
            return report;
        }
        catch (Exception ex)
        {
            report.Abort("Pre-flight check failed: " + Describe(ex));
            return report;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        long sequence = 0;
        try
        {
            await Parallel.ForEachAsync(
                _source.EnumerateAsync(linked.Token),
                new ParallelOptions { MaxDegreeOfParallelism = _options.Parallel, CancellationToken = linked.Token },
                async (item, token) =>
                {
                    ItemResult result;
                    try
                    {
                        result = await ProcessAsync(item, token).ConfigureAwait(false);
                    }
                    catch (FatalMigrationException ex)
                    {
                        // 全件に効く失敗。残りは処理せず止める
                        report.Abort(ex.Message);
                        await linked.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    report.Add(result);
                    _reporter.ItemCompleted(Interlocked.Increment(ref sequence), result);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                report.Abort("Interrupted.");
            }
            else if (!report.Aborted)
            {
                report.Abort("Cancelled.");
            }
        }
        catch (FatalMigrationException ex)
        {
            report.Abort(ex.Message);
        }
        catch (Exception ex)
        {
            // 処理側の例外は ProcessAsync が拾うため、ここへ来るのは一覧の取得の失敗
            report.Abort("Failed to list the source objects: " + Describe(ex));
        }
        return report;
    }

    private async Task<ItemResult> ProcessAsync(SourceObject item, CancellationToken ct)
    {
        if (item.Problem is not null)
        {
            return new ItemResult(item, ItemOutcome.Failed, Error: item.Problem);
        }

        try
        {
            var existingSize = await WithRetry(item, () => _sink.GetSizeAsync(item.ObjectName, ct), ct).ConfigureAwait(false);
            var sameSize = existingSize == item.Size;

            if (_options.DryRun)
            {
                return new ItemResult(
                    item,
                    sameSize ? ItemOutcome.PlannedSkip : ItemOutcome.PlannedCopy,
                    WouldDeleteSource: _options.DeleteSource);
            }

            var skipped = false;
            if (sameSize)
            {
                // 大きさが同じ: 移行元・移行先の両方のハッシュが一致すれば、同じものとしてとばす（再開）
                var sourceHash = await WithRetry(item, () => _source.ComputeHashAsync(item, ct), ct).ConfigureAwait(false);
                var existing = await WithRetry(item, () => _sink.ReadBackAsync(item.ObjectName, ct), ct).ConfigureAwait(false);
                skipped = existing.Size == item.Size && string.Equals(existing.Sha256, sourceHash, StringComparison.Ordinal);
            }

            if (!skipped)
            {
                await CopyAndVerifyAsync(item, ct).ConfigureAwait(false);
            }

            // ここへ来たものは、移行先で大きさとハッシュを確かめ済み。確かめられないものは上で例外になり、消さない
            var deleted = false;
            if (_options.DeleteSource)
            {
                try
                {
                    var deleter = (ISourceDeleter)_source;
                    await WithRetry(item, () => deleter.DeleteSourceAsync(item, ct), ct).ConfigureAwait(false);
                    deleted = true;
                }
                catch (Exception ex) when (ex is not FatalMigrationException && ex is not OperationCanceledException)
                {
                    return new ItemResult(
                        item,
                        ItemOutcome.Failed,
                        Error: "Copied and verified, but the source could not be deleted: " + Describe(ex));
                }
            }

            return new ItemResult(item, skipped ? ItemOutcome.Skipped : ItemOutcome.Copied, SourceDeleted: deleted);
        }
        catch (FatalMigrationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ItemResult(item, ItemOutcome.Failed, Error: Describe(ex));
        }
    }

    private async Task CopyAndVerifyAsync(SourceObject item, CancellationToken ct)
    {
        var copiedHash = await WithRetry(
            item,
            async () =>
            {
                await using var raw = await _source.OpenReadAsync(item, ct).ConfigureAwait(false);
                await using var hashing = new HashingReadStream(raw);
                await _sink.WriteAsync(item.ObjectName, hashing, item.ContentType, ct).ConfigureAwait(false);
                if (!hashing.ReachedEnd)
                {
                    // 書き込み側が末尾まで読まなかった。残りを読み捨て、大きさの食い違いとして検出する
                    await hashing.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                }
                if (hashing.BytesRead != item.Size)
                {
                    throw new MigrationIntegrityException(
                        $"The source size differs from the data read (expected {item.Size} bytes, read {hashing.BytesRead} bytes).");
                }
                return hashing.GetSha256();
            },
            ct).ConfigureAwait(false);

        var readBack = await WithRetry(item, () => _sink.ReadBackAsync(item.ObjectName, ct), ct).ConfigureAwait(false);
        if (readBack.Size != item.Size || !string.Equals(readBack.Sha256, copiedHash, StringComparison.Ordinal))
        {
            // 壊れたコピーを残さない（後始末の失敗は、検証失敗の報告を優先して無視する）
            try
            {
                await _sink.DeleteAsync(item.ObjectName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 無視
            }
            throw new MigrationIntegrityException(
                $"Verification failed: the destination does not match the source (expected {item.Size} bytes sha256 {Short(copiedHash)}, "
                + $"destination has {readBack.Size} bytes sha256 {Short(readBack.Sha256)}).");
        }
    }

    private Task<T> WithRetry<T>(SourceObject item, Func<Task<T>> operation, CancellationToken ct)
        => Retry.RunAsync(
            operation,
            _options.MaxRetries,
            _options.RetryDelay,
            (attempt, ex) => _reporter.Retrying(item.ObjectName, attempt, _options.MaxRetries, Describe(ex)),
            ct);

    private Task WithRetry(SourceObject item, Func<Task> operation, CancellationToken ct)
        => Retry.RunAsync(
            operation,
            _options.MaxRetries,
            _options.RetryDelay,
            (attempt, ex) => _reporter.Retrying(item.ObjectName, attempt, _options.MaxRetries, Describe(ex)),
            ct);

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    /// <summary>失敗の理由を、1 行の英語の文字列にする。</summary>
    internal static string Describe(Exception ex)
    {
        var message = ex is MigrationIntegrityException or FatalMigrationException
            ? ex.Message
            : ex.GetType().Name + ": " + ex.Message;
        return message.ReplaceLineEndings(" ").Trim();
    }
}
