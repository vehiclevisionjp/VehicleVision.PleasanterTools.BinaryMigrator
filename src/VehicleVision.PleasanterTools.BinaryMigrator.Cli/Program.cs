using System.Reflection;
using VehicleVision.PleasanterTools.BinaryMigrator.Cli;
using VehicleVision.PleasanterTools.BinaryMigrator.Core;

// コンソールへ出す文字列は英語（Azure の Kudu の Debug console では日本語が化けるため）。
// 終了コード: 0 = 全件成功 / 1 = 失敗あり・途中で停止 / 2 = 引数・設定の誤り
var parsed = CommandLine.Parse(args, Environment.GetEnvironmentVariable);
switch (parsed.Kind)
{
    case CommandKind.Help:
        Console.Out.WriteLine(CommandLine.HelpText);
        return 0;

    case CommandKind.Version:
        Console.Out.WriteLine(
            typeof(ConsoleOutput).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown");
        return 0;

    case CommandKind.Error:
        Console.Error.WriteLine("Error: " + ReportFormatter.ToAscii(parsed.Error ?? "invalid arguments"));
        Console.Error.WriteLine("Use --help for usage.");
        return 2;
}

var options = parsed.Options!;
MigrationPlan plan;
try
{
    plan = MigrationPlan.Create(options, Environment.GetEnvironmentVariable, () => Console.In, File.ReadAllText);
}
catch (FatalMigrationException ex)
{
    Console.Error.WriteLine("Error: " + ReportFormatter.ToAscii(ex.Message));
    return 2;
}

var output = new ConsoleOutput(plan.Redactor);
output.Line("Source      : " + plan.Source.DisplayName);
output.Line("Destination : " + plan.Sink.DisplayName);
output.Line("Types       : " + string.Join(", ", options.Types));
output.Line("Parallel    : " + options.Parallel);
if (options.DryRun)
{
    output.Line("Mode        : DRY RUN (nothing is written or deleted)");
}
else if (options.DeleteSource)
{
    output.Line("Mode        : COPY, VERIFY, then DELETE THE SOURCE (confirmed by --yes)");
}
else
{
    output.Line("Mode        : COPY and VERIFY (the source is kept)");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var migrator = new Migrator(plan.Source, plan.Sink, plan.Options, new ConsoleReporter(output, options.Quiet));
MigrationReport report;
try
{
    report = await migrator.RunAsync(cts.Token);
}
catch (Exception ex) when (ex is ArgumentException)
{
    Console.Error.WriteLine("Error: " + ReportFormatter.ToAscii(plan.Redactor.Redact(ex.Message)));
    return 2;
}

foreach (var line in ReportFormatter.FormatSummary(report))
{
    output.Line(line);
}
return report.Succeeded ? 0 : 1;
