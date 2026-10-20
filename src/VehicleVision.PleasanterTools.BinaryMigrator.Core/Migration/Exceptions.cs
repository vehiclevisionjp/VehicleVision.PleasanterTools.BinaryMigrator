namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// 全件に効く失敗（接続できない・認証できない・保管先が無いなど）。1 件の失敗とは違い、移行全体を即座に止める。
/// メッセージは英語（コンソールへ出す）。
/// </summary>
public sealed class FatalMigrationException : Exception
{
    public FatalMigrationException(string message) : base(message) { }

    public FatalMigrationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// 整合の不一致など、再試行しても直らない 1 件の失敗。メッセージは英語。
/// </summary>
public sealed class MigrationIntegrityException : Exception
{
    public MigrationIntegrityException(string message) : base(message) { }
}
