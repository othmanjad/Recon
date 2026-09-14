using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Domain.Money;
using Recon.Engine;
using Recon.Engine.Parsing;
using Recon.Engine.Staging;

namespace Recon.Cli;

/// <summary>
/// The batch entry point: acquire, parse, stage, then reconcile.
///
/// <para>
/// Deliberately thin. Everything it does is available as a library call so the
/// scheduler, the portal's manual trigger and the integration tests all drive
/// the same code — a CLI that reimplements the pipeline is a second pipeline
/// that drifts.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "run" => await RunAsync(args).ConfigureAwait(false),
                "load" => await LoadAsync(args).ConfigureAwait(false),
                "validate" => await ValidateAsync(args).ConfigureAwait(false),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException
                                      or ArgumentException or IOException)
        {
            // An operator needs the reason, not a stack trace. Anything not
            // listed here is a bug and should surface in full.
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static void Usage() => Console.WriteLine(
        """
        recon — reconciliation platform runner

          recon validate --conn <cs> --definition <id>
              Report whether a definition can be activated, and why not.

          recon load --conn <cs> --dataset <id> --file <path>
              --business-date <yyyy-MM-dd> [--run <id>] [--format <id>]
              Parse and stage a file. Prints rows staged and elapsed time.

          recon run --conn <cs> --definition <id> --business-date <yyyy-MM-dd>
              [--left-file <path>] [--right-file <path>]
              [--session <ref>] [--type Scheduled|Manual|Rerun|Rematch|Sandbox]
              [--source-run <id>] [--by <user>]
              Execute a reconciliation and print the pass distribution and
              control totals. Given --left-file / --right-file it also
              acquires, parses and stages them first — the whole pipeline in
              one command, which is the order the stages actually run in.

        Amounts are printed in minor units scaled by the dataset's currency.
        """);

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'. Try --help.");
        return 2;
    }

    // =================================================================

    private static async Task<int> ValidateAsync(string[] args)
    {
        var connectionString = Required(args, "--conn");
        var definitionId = int.Parse(Required(args, "--definition"), CultureInfo.InvariantCulture);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);

        Console.WriteLine($"{definition.Code} — {definition.Name}");
        Console.WriteLine($"  left   {definition.Left.Code} ({definition.Left.Fields.Count} fields)");
        Console.WriteLine($"  right  {definition.Right.Code} ({definition.Right.Fields.Count} fields)");
        Console.WriteLine($"  passes {definition.ActivePasses.Count()}");
        Console.WriteLine($"  window -{definition.MatchingWindowDaysBefore} / " +
                          $"+{definition.MatchingWindowDaysAfter} days");

        var problems = definition.ActivationProblems();

        if (problems.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("ready to activate.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"cannot activate — {problems.Count} problem(s):");
        foreach (var problem in problems)
        {
            Console.WriteLine("  - " + problem);
        }

        return 1;
    }

    private static async Task<int> LoadAsync(string[] args)
    {
        var connectionString = Required(args, "--conn");
        var datasetId = int.Parse(Required(args, "--dataset"), CultureInfo.InvariantCulture);
        var path = Required(args, "--file");
        var businessDate = DateOnly.Parse(Required(args, "--business-date"), CultureInfo.InvariantCulture);
        var runId = long.Parse(Optional(args, "--run") ?? "0", CultureInfo.InvariantCulture);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var dataset = await config.LoadDatasetAsync(datasetId).ConfigureAwait(false);
        var currencies = await config.LoadCurrenciesAsync().ConfigureAwait(false);
        var settings = await config.LoadSettingsAsync().ConfigureAwait(false);

        var currency = currencies[dataset.DefaultCurrency ?? "JOD"];
        var format = await FileFormatLoader.LoadAsync(connection, dataset, businessDate)
            .ConfigureAwait(false);

        var batchSize = settings.TryGetValue("BulkCopyBatchSize", out var raw)
            && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 100_000;

        var parser = new RowParser(dataset, format, currency);
        var reader = new CsvReader(format.Csv);
        var loader = new StagingBulkLoader(connection, new StagingLoadOptions { BatchSize = batchSize });

        var errors = 0;
        var stopwatch = Stopwatch.StartNew();

        using var file = File.OpenText(path);

        var result = await loader.LoadAsync(Records()).ConfigureAwait(false);
        stopwatch.Stop();

        Console.WriteLine($"staged {result.RowsWritten:N0} rows in {result.Elapsed.TotalSeconds:N1}s " +
                          $"({result.RowsPerSecond:N0} rows/s), {errors:N0} rejected");

        return errors > format.MaxParseErrors ? 1 : 0;

        IEnumerable<StagingRecord> Records()
        {
            var record = new StagingRecord { DatasetId = datasetId, LoadRunId = runId };

            foreach (var row in reader.Read(file))
            {
                if (parser.TryParse(row, record, businessDate, out var failure))
                {
                    yield return record;
                    continue;
                }

                errors++;

                // E4: a wrong-format file would otherwise produce one error row
                // per source row, each carrying its raw line.
                if (errors > format.MaxParseErrors)
                {
                    throw new InvalidOperationException(
                        $"parse errors exceeded MaxParseErrors ({format.MaxParseErrors}); " +
                        $"the file is being treated as the wrong format. " +
                        $"First problem at line {failure!.RawRowNumber}: {failure.Message}");
                }
            }
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var connectionString = Required(args, "--conn");
        var definitionId = int.Parse(Required(args, "--definition"), CultureInfo.InvariantCulture);
        var businessDate = DateOnly.Parse(Required(args, "--business-date"), CultureInfo.InvariantCulture);
        var sessionRef = Optional(args, "--session");
        var runType = Enum.Parse<RunType>(Optional(args, "--type") ?? "Manual");
        var sourceRun = Optional(args, "--source-run") is { } s
            ? long.Parse(s, CultureInfo.InvariantCulture)
            : (long?)null;
        var triggeredBy = Optional(args, "--by") ?? Environment.UserName;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        var config = new ConfigurationRepository(connection);
        var runs = new RunRepository(connection);
        var definition = await config.LoadDefinitionAsync(definitionId).ConfigureAwait(false);

        var problems = definition.ActivationProblems();
        if (problems.Count > 0 && runType != RunType.Sandbox)
        {
            // A sandbox run against a half-built definition is the point of
            // sandbox runs; a scheduled one is not.
            await Console.Error.WriteLineAsync(
                "definition cannot be activated: " + string.Join("; ", problems)).ConfigureAwait(false);
            return 1;
        }

        // B2: the scheduler and a manual trigger must not execute the same
        // definition and date at once. The second attempt is recorded rather
        // than left to race.
        if (!await runs.TryAcquireRunLockAsync(definitionId, businessDate, sessionRef)
                .ConfigureAwait(false))
        {
            await runs.RecordRejectedAsync(
                definitionId, definition.Version, businessDate, sessionRef, runType,
                triggeredBy, "already running").ConfigureAwait(false);

            await Console.Error.WriteLineAsync(
                "another run holds the lock for this definition and business date.").ConfigureAwait(false);
            return 3;
        }

        try
        {
            var run = await runs.CreateRunAsync(
                definition, businessDate, sessionRef, runType, triggeredBy, sourceRun)
                .ConfigureAwait(false);

            Console.WriteLine($"run {run.RunId} ({runType}) — staging from run {run.StagingRunId}");

            // Rows are staged UNDER a run id, so the load has to follow the
            // run's creation. Splitting the two into separate commands left
            // no way to name the run to stage into, which is why they are one
            // command here.
            var leftFile = Optional(args, "--left-file");
            var rightFile = Optional(args, "--right-file");

            if (leftFile is not null)
            {
                await StageFileAsync(connection, config, runs, definition.Left,
                    leftFile, businessDate, run.RunId, Side.Left).ConfigureAwait(false);
            }

            if (rightFile is not null)
            {
                await StageFileAsync(connection, config, runs, definition.Right,
                    rightFile, businessDate, run.RunId, Side.Right).ConfigureAwait(false);
            }

            var runner = new ReconciliationRunner(connection, runs);
            var outcome = await runner.ExecuteAsync(definition, run).ConfigureAwait(false);

            Report(outcome, definition);
            return outcome.Status == RunStatus.Completed ? 0 : 1;
        }
        finally
        {
            await runs.ReleaseRunLockAsync(definitionId, businessDate, sessionRef)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses and stages one file, recording the Parse and Stage stages as run
    /// steps so a resumed run skips them and the timings appear on the
    /// dashboard alongside the passes.
    /// </summary>
    private static async Task StageFileAsync(
        SqlConnection connection,
        ConfigurationRepository config,
        RunRepository runs,
        Dataset dataset,
        string path,
        DateOnly businessDate,
        long runId,
        Side side)
    {
        var step = await runs.BeginStepAsync(runId, RunStepName.Parse, null, side)
            .ConfigureAwait(false);

        if (step.AlreadyCompleted)
        {
            Console.WriteLine($"  {side,-5} {dataset.Code} already staged; skipping");
            return;
        }

        try
        {
            var currencies = await config.LoadCurrenciesAsync().ConfigureAwait(false);
            var settings = await config.LoadSettingsAsync().ConfigureAwait(false);
            var format = await FileFormatLoader.LoadAsync(connection, dataset, businessDate)
                .ConfigureAwait(false);

            var currency = currencies[dataset.DefaultCurrency ?? "JOD"];
            var batchSize = settings.TryGetValue("BulkCopyBatchSize", out var raw)
                && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 100_000;

            var parser = new RowParser(dataset, format, currency);
            var reader = new CsvReader(format.Csv);
            var loader = new StagingBulkLoader(
                connection, new StagingLoadOptions { BatchSize = batchSize });

            var errors = new List<ParseFailure>();
            using var file = File.OpenText(path);

            var result = await loader.LoadAsync(Records()).ConfigureAwait(false);

            // Rejected rows go to stg.ParseError with their line, field, reason
            // and raw line. A console count is not something an operator can
            // act on.
            if (errors.Count > 0)
            {
                await new ParseErrorWriter(connection).WriteAsync(
                    runId, businessDate, sourceFileId: null,
                    errors.Select(e => new ParseErrorRow(
                        e.Type.ToString(), e.FieldCode, e.Message, e.RawRowNumber, e.RawLine))
                        .ToList()).ConfigureAwait(false);
            }

            Console.WriteLine($"  {side,-5} {dataset.Code}: staged {result.RowsWritten:N0} rows " +
                              $"in {result.Elapsed.TotalSeconds:N1}s, {errors.Count:N0} rejected");

            foreach (var failure in errors.Take(5))
            {
                Console.WriteLine($"         line {failure.RawRowNumber}: {failure.Message}");
            }

            if (errors.Count > 5)
            {
                Console.WriteLine($"         ... and {errors.Count - 5:N0} more " +
                                  "(all of them are in stg.ParseError)");
            }

            await runs.CompleteStepAsync(step.RunStepId, result.RowsWritten, null, null)
                .ConfigureAwait(false);

            IEnumerable<StagingRecord> Records()
            {
                var record = new StagingRecord { DatasetId = dataset.DatasetId, LoadRunId = runId };

                foreach (var row in reader.Read(file))
                {
                    if (parser.TryParse(row, record, businessDate, out var failure))
                    {
                        yield return record;
                        continue;
                    }

                    errors.Add(failure!);

                    // E4: a wrong-format file would otherwise produce one error
                    // per source row, each carrying its raw line.
                    if (errors.Count > format.MaxParseErrors)
                    {
                        throw new InvalidOperationException(
                            $"{dataset.Code}: parse errors exceeded MaxParseErrors " +
                            $"({format.MaxParseErrors}), so the file is being treated as the " +
                            $"wrong format. First problem at line {failure!.RawRowNumber}: " +
                            failure.Message);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException)
        {
            await runs.FailStepAsync(step.RunStepId, ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    private static void Report(RunOutcome outcome, ReconciliationDefinition definition)
    {
        var currency = definition.Left.DefaultCurrency ?? "JOD";
        var scale = currency == "JOD" ? 3 : 2;

        Console.WriteLine();
        Console.WriteLine($"status      {outcome.Status}");
        Console.WriteLine($"left rows   {outcome.Counts.LeftRows:N0}");
        Console.WriteLine($"right rows  {outcome.Counts.RightRows:N0}");
        Console.WriteLine($"matched     {outcome.Counts.Matched:N0}");
        Console.WriteLine($"unmatched   {outcome.Counts.Unmatched:N0}");
        Console.WriteLine($"ambiguous   {outcome.Counts.Ambiguous:N0}");

        if (outcome.Passes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("match distribution");
            var bySequence = outcome.Passes.ToDictionary(p => p.Sequence);
            var passShares = outcome.Distribution();

            foreach (var (sequence, code, share) in passShares)
            {
                var matched = bySequence[sequence].Matched;
                Console.WriteLine($"  {sequence}  {code,-16} {matched,12:N0}  {share,6:N2}%");
            }

            // §9.3: the distribution across passes is a data-quality early
            // warning, so the CLI says so rather than leaving it to be noticed.
            var last = passShares.Count > 0 ? passShares[^1] : default;
            if (last.Share > 15)
            {
                Console.WriteLine();
                Console.WriteLine($"  warning: the last pass carried {last.Share:N2}% of matches. " +
                                  "The clean reference match is degrading upstream " +
                                  "(MatchDistributionDrift).");
            }
        }

        if (outcome.ControlTotals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("control totals");
            foreach (var total in outcome.ControlTotals)
            {
                var flag = total.IsBalanced ? "ok " : "OUT";

                // A count is a count. Formatting one as money turned 15
                // matched rows into "0.015".
                string Show(long value) => total.IsAmount
                    ? MinorUnits.Format(value, scale)
                    : value.ToString("N0", CultureInfo.InvariantCulture);

                Console.WriteLine($"  {flag} {total.CheckCode,-20} " +
                                  $"{Show(total.ValueA),18} vs {Show(total.ValueB),18}  " +
                                  $"diff {Show(total.Difference)}" +
                                  (total.IsBalanced ? string.Empty
                                   : total.FailsRun ? "   (fails the run)"
                                   : "   (reported, does not fail the run)"));
            }
        }

        if (outcome.Error is not null)
        {
            Console.WriteLine();
            Console.WriteLine("failed: " + outcome.Error);
        }
    }

    // =================================================================

    private static string Required(string[] args, string name) =>
        Optional(args, name)
        ?? throw new ArgumentException($"{name} is required. Try --help.", nameof(args));

    private static string? Optional(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
