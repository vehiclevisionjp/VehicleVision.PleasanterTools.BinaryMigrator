namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>解析済みの指定から作った、実行できる移行の一式。</summary>
public sealed class MigrationPlan
{
    public required IBinarySource Source { get; init; }

    public required IBinarySink Sink { get; init; }

    public required MigratorOptions Options { get; init; }

    /// <summary>コンソールへ出す文字列から伏せる秘密。</summary>
    public required SecretRedactor Redactor { get; init; }

    /// <summary>
    /// 解析済みの指定から、移行元・移行先を組み立てる。ここでは DB・Azure へ接続しない（接続は実行時）。
    /// 設定の誤りは <see cref="FatalMigrationException"/>（メッセージは英語）。
    /// </summary>
    public static MigrationPlan Create(
        MigrateCommandOptions options,
        Func<string, string?> getEnvironmentVariable,
        Func<TextReader> openStandardInput,
        Func<string, string> readAllText)
    {
        ArgumentNullException.ThrowIfNull(options);
        var redactor = new SecretRedactor();
        var groups = options.Types;

        IBinarySource source;
        switch (options.From)
        {
            case StorageKind.Rds:
            {
                var connectionString = SecretResolver.Resolve(
                    options.RdsConnectionString, EnvironmentNames.RdsConnectionString, getEnvironmentVariable, openStandardInput, readAllText)
                    ?? throw new FatalMigrationException("The Rds connection string is empty.");
                var writeConnectionString = NullIfBlank(getEnvironmentVariable(EnvironmentNames.RdsWriteConnectionString));
                redactor.Add(connectionString);
                redactor.Add(writeConnectionString);
                source = new RdsBinarySource(
                    new RdsConnection(options.RdsDbms!.Value, connectionString, writeConnectionString),
                    groups);
                break;
            }
            case StorageKind.Local:
                source = new LocalBinaryStore(options.LocalPath!, groups);
                break;
            case StorageKind.Azure:
                source = CreateAzure(options, groups, redactor, getEnvironmentVariable, openStandardInput, readAllText);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        IBinarySink sink = options.To switch
        {
            StorageKind.Local => new LocalBinaryStore(options.LocalPath!, groups),
            StorageKind.Azure => CreateAzure(options, groups, redactor, getEnvironmentVariable, openStandardInput, readAllText),
            _ => throw new FatalMigrationException("Writing to Rds is not supported."),
        };

        return new MigrationPlan
        {
            Source = source,
            Sink = sink,
            Redactor = redactor,
            Options = new MigratorOptions
            {
                DryRun = options.DryRun,
                DeleteSource = options.DeleteSource,
                Parallel = options.Parallel,
                MaxRetries = options.MaxRetries,
            },
        };
    }

    private static AzureBlobBinaryStore CreateAzure(
        MigrateCommandOptions options,
        IReadOnlyList<BinaryGroup> groups,
        SecretRedactor redactor,
        Func<string, string?> getEnvironmentVariable,
        Func<TextReader> openStandardInput,
        Func<string, string> readAllText)
    {
        var connectionString = SecretResolver.Resolve(
            options.AzureConnectionString, EnvironmentNames.AzureConnectionString, getEnvironmentVariable, openStandardInput, readAllText);
        redactor.Add(connectionString);

        Uri? accountUri = null;
        if (!string.IsNullOrWhiteSpace(options.AzureAccountUri)
            && !Uri.TryCreate(options.AzureAccountUri, UriKind.Absolute, out accountUri))
        {
            throw new FatalMigrationException("The Azure account URI is not a valid absolute URI.");
        }

        var connection = new AzureBlobConnection(options.AzureContainer ?? string.Empty, accountUri, connectionString);
        var problem = connection.FindProblem();
        if (problem is not null)
        {
            throw new FatalMigrationException(problem);
        }
        return AzureBlobBinaryStore.Create(connection, groups);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
