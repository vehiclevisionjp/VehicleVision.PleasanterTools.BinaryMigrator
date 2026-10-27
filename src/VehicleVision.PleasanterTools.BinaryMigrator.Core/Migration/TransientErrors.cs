using System.Data.Common;
using Azure;

namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// 再試行してよい一時的な失敗（ネットワーク・タイムアウト・サーバーの過負荷）の判定。
/// 認証・権限・整合の不一致・入力の誤りは再試行しない。
/// </summary>
public static class TransientErrors
{
    public static bool IsTransient(Exception exception)
    {
        switch (exception)
        {
            case null:
                return false;
            case FatalMigrationException:
            case MigrationIntegrityException:
            case OperationCanceledException:
            case ArgumentException:
            case UnauthorizedAccessException:
            case FileNotFoundException:
            case DirectoryNotFoundException:
            case PathTooLongException:
                return false;
            case TimeoutException:
            case HttpRequestException:
                return true;
            case RequestFailedException requestFailed:
                // Status 0 はサーバーから応答が無かった（通信の失敗）
                return requestFailed.Status is 0 or 408 or 429 or 500 or 502 or 503 or 504
                    || requestFailed.InnerException is not null && IsTransient(requestFailed.InnerException);
            case DbException db:
                return db.IsTransient;
            case IOException:
                return true;
            case AggregateException aggregate:
                return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsTransient);
            default:
                return exception.InnerException is not null && IsTransient(exception.InnerException);
        }
    }
}

/// <summary>一時的な失敗に限り、少数回だけやり直す。</summary>
internal static class Retry
{
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        int maxRetries,
        TimeSpan baseDelay,
        Action<int, Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && TransientErrors.IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                onRetry?.Invoke(attempt + 1, ex);
                if (baseDelay > TimeSpan.Zero)
                {
                    // 1 回目 = 基準、2 回目 = 2 倍、3 回目 = 4 倍 ...（上限 30 秒）
                    var delay = TimeSpan.FromTicks(Math.Min(baseDelay.Ticks << attempt, TimeSpan.FromSeconds(30).Ticks));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public static async Task RunAsync(
        Func<Task> operation,
        int maxRetries,
        TimeSpan baseDelay,
        Action<int, Exception>? onRetry,
        CancellationToken cancellationToken)
        => await RunAsync(
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            maxRetries,
            baseDelay,
            onRetry,
            cancellationToken).ConfigureAwait(false);
}
