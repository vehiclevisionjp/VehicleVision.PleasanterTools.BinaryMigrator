namespace VehicleVision.PleasanterTools.BinaryMigrator.Core;

/// <summary>
/// コマンドラインの解析。引数解析のライブラリは使わない（依存を増やさない）。
/// 解析は純粋な処理で、環境変数は関数で受け取る。ファイル・標準入力の中身はここでは読まない。
/// 出力する文字列は英語（Azure の Kudu の Debug console で日本語が化けるため）。
/// </summary>
public static class CommandLine
{
    public const string ProgramName = "binary-migrator";

    public const string HelpText = """
        binary-migrator - move Pleasanter binaries (attachments and images) between Rds, Local and AzureBlob.

        Usage:
          binary-migrator migrate --from <rds|local|azure> --to <local|azure> [options]
          binary-migrator --help | --version

        Directions: rds->local, rds->azure, local->azure, azure->local. Writing back to Rds is not supported.
        The source is NOT deleted unless you pass --delete-source. Always try --dry-run first, and back up first.
        BinaryStorage.json of Pleasanter is never modified; switch its Provider yourself after the migration.

        Common options:
          --dry-run                 Show what would be copied/skipped/deleted. Nothing is written or deleted.
          --delete-source           After a copy is verified (size and SHA-256 read back from the destination),
                                    delete the source: Rds sets the column (Bin) to NULL, Local/AzureBlob delete
                                    the object. Unverified items are never deleted. Requires --yes.
          --yes, -y                 Confirm --delete-source.
          --types <list>            Comma-separated: attachments,images,siteimage,tenantimage (default: all).
          --parallel <N>            Concurrent copies, 1-16 (default: 1, serial).
          --max-retries <N>         Retries for transient failures, 0-10 (default: 3).
          --quiet                   Do not print one line per successful item.
          --help, -h                Show this help.
          --version                 Show the version.

        Rds source options (connects to the Pleasanter database directly):
          --rds-dbms <name>                     sqlserver | postgresql | mysql   [env BINARY_MIGRATOR_RDS_DBMS]
          --rds-connection-string-file <path>   Read the connection string from a file.
          --rds-connection-string-stdin         Read the connection string from standard input.
          (default: env BINARY_MIGRATOR_RDS_CONNECTION_STRING)
          (optional, for --delete-source: env BINARY_MIGRATOR_RDS_WRITE_CONNECTION_STRING, a login that may UPDATE)
          A connection string is never accepted as a plain command-line argument (it contains a password).

        Local options:
          --local-path <dir>        Root folder of the binary storage   [env BINARY_MIGRATOR_LOCAL_PATH]
                                    (BinaryStorage.json "Path", or App_Data/BinaryStorage of Pleasanter).

        AzureBlob options:
          --azure-container <name>              Container name          [env BINARY_MIGRATOR_AZURE_CONTAINER]
          --azure-account-uri <uri>             Storage account URI     [env BINARY_MIGRATOR_AZURE_ACCOUNT_URI]
                                                (authenticates with DefaultAzureCredential)
          --azure-connection-string-file <path> Use a connection string read from a file instead.
          --azure-connection-string-stdin       Use a connection string read from standard input instead.
          (default: env BINARY_MIGRATOR_AZURE_CONNECTION_STRING)

        Exit codes: 0 = all succeeded, 1 = some items failed or the run was stopped, 2 = invalid arguments.
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--dry-run", "--delete-source", "--yes", "-y", "--quiet",
        "--rds-connection-string-stdin", "--azure-connection-string-stdin",
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--from", "--to", "--parallel", "--max-retries", "--types",
        "--rds-dbms", "--rds-connection-string-file",
        "--local-path",
        "--azure-account-uri", "--azure-container", "--azure-connection-string-file",
    };

    public static ParsedCommand Parse(IReadOnlyList<string> args, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (args.Count == 0)
        {
            return Fail("No command was given. Use: " + ProgramName + " migrate --from <rds|local|azure> --to <local|azure>");
        }
        if (args[0] == "help" || args.Any(a => a is "--help" or "-h"))
        {
            return new ParsedCommand(CommandKind.Help);
        }
        if (args.Count == 1 && args[0] == "--version")
        {
            return new ParsedCommand(CommandKind.Version);
        }
        if (args[0] != "migrate")
        {
            return Fail($"Unknown command '{args[0]}'. The only command is 'migrate'.");
        }

        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            string? inlineValue = null;
            var name = token;
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = token.IndexOf('=');
                if (eq > 0)
                {
                    name = token[..eq];
                    inlineValue = token[(eq + 1)..];
                }
            }

            if (name is "--rds-connection-string" or "--azure-connection-string")
            {
                return Fail(
                    $"{name} is not supported: a connection string on the command line exposes the password. "
                    + $"Use {name}-file, {name}-stdin, or the environment variable.");
            }
            if (Flags.Contains(name))
            {
                if (inlineValue is not null)
                {
                    return Fail($"{name} does not take a value.");
                }
                flags.Add(name);
            }
            else if (ValueOptions.Contains(name))
            {
                if (inlineValue is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        return Fail($"{name} needs a value.");
                    }
                    inlineValue = args[++i];
                }
                values[name] = inlineValue;
            }
            else
            {
                return Fail($"Unknown option or argument '{token}'. Use --help.");
            }
        }

        // --from / --to
        if (!values.TryGetValue("--from", out var fromText) || !values.TryGetValue("--to", out var toText))
        {
            return Fail("--from and --to are required.");
        }
        if (!TryParseKind(fromText, out var from))
        {
            return Fail($"Unknown --from '{fromText}'. Use rds, local or azure.");
        }
        if (toText.Equals("rds", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("Writing to Rds is not supported. The destination must be local or azure.");
        }
        if (!TryParseKind(toText, out var to))
        {
            return Fail($"Unknown --to '{toText}'. Use local or azure.");
        }
        if (from == to)
        {
            return Fail("--from and --to must differ.");
        }

        // 数値と種別
        var parallel = 1;
        if (values.TryGetValue("--parallel", out var parallelText)
            && (!int.TryParse(parallelText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parallel) || parallel is < 1 or > 16))
        {
            return Fail("--parallel must be an integer from 1 to 16.");
        }
        var maxRetries = 3;
        if (values.TryGetValue("--max-retries", out var retriesText)
            && (!int.TryParse(retriesText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out maxRetries) || maxRetries is < 0 or > 10))
        {
            return Fail("--max-retries must be an integer from 0 to 10.");
        }
        IReadOnlyList<BinaryGroup> types = BinaryGroups.All;
        if (values.TryGetValue("--types", out var typesText) && !BinaryGroups.TryParse(typesText, out types, out var typesError))
        {
            return Fail(typesError!);
        }

        // 保管先ごとの指定（引数が環境変数より優先）
        string? Value(string option, string environmentName)
            => values.TryGetValue(option, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : NullIfBlank(getEnvironmentVariable(environmentName));

        RdsDbms? dbms = null;
        var rdsSecret = SecretSource.None;
        var rdsStdin = flags.Contains("--rds-connection-string-stdin");
        var azureStdin = flags.Contains("--azure-connection-string-stdin");
        values.TryGetValue("--rds-connection-string-file", out var rdsFile);
        values.TryGetValue("--azure-connection-string-file", out var azureFile);

        if (from == StorageKind.Rds)
        {
            var dbmsText = Value("--rds-dbms", EnvironmentNames.RdsDbms);
            if (dbmsText is null)
            {
                return Fail("The Rds DBMS is required: --rds-dbms sqlserver|postgresql|mysql (or env " + EnvironmentNames.RdsDbms + ").");
            }
            if (!RdsDbmsNames.TryParse(dbmsText, out var parsedDbms))
            {
                return Fail($"Unknown --rds-dbms '{dbmsText}'. Use sqlserver, postgresql or mysql.");
            }
            dbms = parsedDbms;
            if (rdsFile is not null && rdsStdin)
            {
                return Fail("Use only one of --rds-connection-string-file and --rds-connection-string-stdin.");
            }
            if (rdsFile is null && !rdsStdin && NullIfBlank(getEnvironmentVariable(EnvironmentNames.RdsConnectionString)) is null)
            {
                return Fail(
                    "The Rds connection string is required: set env " + EnvironmentNames.RdsConnectionString
                    + ", or use --rds-connection-string-file / --rds-connection-string-stdin.");
            }
            rdsSecret = new SecretSource(rdsFile, rdsStdin);
        }

        string? localPath = null;
        if (from == StorageKind.Local || to == StorageKind.Local)
        {
            localPath = Value("--local-path", EnvironmentNames.LocalPath);
            if (localPath is null)
            {
                return Fail("The local folder is required: --local-path <dir> (or env " + EnvironmentNames.LocalPath + ").");
            }
        }

        string? azureUri = null;
        string? azureContainer = null;
        var azureSecret = SecretSource.None;
        if (from == StorageKind.Azure || to == StorageKind.Azure)
        {
            azureContainer = Value("--azure-container", EnvironmentNames.AzureContainer);
            if (azureContainer is null)
            {
                return Fail("The Azure container is required: --azure-container <name> (or env " + EnvironmentNames.AzureContainer + ").");
            }
            azureUri = Value("--azure-account-uri", EnvironmentNames.AzureAccountUri);
            if (azureFile is not null && azureStdin)
            {
                return Fail("Use only one of --azure-connection-string-file and --azure-connection-string-stdin.");
            }
            var hasConnectionString = azureFile is not null
                || azureStdin
                || NullIfBlank(getEnvironmentVariable(EnvironmentNames.AzureConnectionString)) is not null;
            if (!hasConnectionString && azureUri is null)
            {
                return Fail(
                    "Azure access is required: --azure-account-uri <uri> (DefaultAzureCredential), "
                    + "or a connection string via env " + EnvironmentNames.AzureConnectionString + " / --azure-connection-string-file / --azure-connection-string-stdin.");
            }
            azureSecret = new SecretSource(azureFile, azureStdin);
        }

        if (rdsStdin && azureStdin)
        {
            return Fail("Standard input can supply only one connection string. Use a file or an environment variable for the other.");
        }

        var dryRun = flags.Contains("--dry-run");
        var deleteSource = flags.Contains("--delete-source");
        var yes = flags.Contains("--yes") || flags.Contains("-y");
        if (deleteSource && !dryRun && !yes)
        {
            return Fail(
                "--delete-source DELETES the source after each verified copy"
                + (from == StorageKind.Rds ? " (Rds: sets the column to NULL)" : string.Empty)
                + ". Back up first and run --dry-run to review. Add --yes to confirm.");
        }

        return new ParsedCommand(CommandKind.Migrate, new MigrateCommandOptions
        {
            From = from,
            To = to,
            DryRun = dryRun,
            DeleteSource = deleteSource,
            Yes = yes,
            Quiet = flags.Contains("--quiet"),
            Parallel = parallel,
            MaxRetries = maxRetries,
            Types = types,
            RdsDbms = dbms,
            RdsConnectionString = rdsSecret,
            LocalPath = localPath,
            AzureAccountUri = azureUri,
            AzureContainer = azureContainer,
            AzureConnectionString = azureSecret,
        });
    }

    private static ParsedCommand Fail(string message) => new(CommandKind.Error, Error: message);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryParseKind(string text, out StorageKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "rds":
                kind = StorageKind.Rds;
                return true;
            case "local":
                kind = StorageKind.Local;
                return true;
            case "azure" or "azureblob":
                kind = StorageKind.Azure;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
