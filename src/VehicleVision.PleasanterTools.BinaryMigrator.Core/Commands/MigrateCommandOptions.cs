namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>保管先の種類。</summary>
public enum StorageKind
{
    Rds,
    Local,
    Azure,
}

/// <summary>秘密（接続文字列）の取得元。コマンドラインへ直接書かせない。</summary>
/// <param name="File">秘密を書いたファイルのパス。</param>
/// <param name="FromStdin">標準入力から読む。</param>
public sealed record SecretSource(string? File = null, bool FromStdin = false)
{
    public static SecretSource None { get; } = new();
}

/// <summary>環境変数の名前。</summary>
public static class EnvironmentNames
{
    public const string RdsDbms = "BINARY_MIGRATOR_RDS_DBMS";
    public const string RdsConnectionString = "BINARY_MIGRATOR_RDS_CONNECTION_STRING";
    public const string RdsWriteConnectionString = "BINARY_MIGRATOR_RDS_WRITE_CONNECTION_STRING";
    public const string LocalPath = "BINARY_MIGRATOR_LOCAL_PATH";
    public const string AzureAccountUri = "BINARY_MIGRATOR_AZURE_ACCOUNT_URI";
    public const string AzureContainer = "BINARY_MIGRATOR_AZURE_CONTAINER";
    public const string AzureConnectionString = "BINARY_MIGRATOR_AZURE_CONNECTION_STRING";
}

/// <summary><c>migrate</c> コマンドの指定（解析済み）。</summary>
public sealed record MigrateCommandOptions
{
    public StorageKind From { get; init; }

    public StorageKind To { get; init; }

    public bool DryRun { get; init; }

    public bool DeleteSource { get; init; }

    /// <summary><c>--delete-source</c> の確認（<c>--yes</c>）。</summary>
    public bool Yes { get; init; }

    /// <summary>成功した件の行を出さない（失敗・要約は出す）。</summary>
    public bool Quiet { get; init; }

    public int Parallel { get; init; } = 1;

    public int MaxRetries { get; init; } = 3;

    public IReadOnlyList<BinaryGroup> Types { get; init; } = BinaryGroups.All;

    public RdsDbms? RdsDbms { get; init; }

    public SecretSource RdsConnectionString { get; init; } = SecretSource.None;

    public string? LocalPath { get; init; }

    public string? AzureAccountUri { get; init; }

    public string? AzureContainer { get; init; }

    public SecretSource AzureConnectionString { get; init; } = SecretSource.None;
}

public enum CommandKind
{
    Migrate,
    Help,
    Version,
    Error,
}

/// <summary>コマンドラインの解析結果。</summary>
public sealed record ParsedCommand(CommandKind Kind, MigrateCommandOptions? Options = null, string? Error = null);
