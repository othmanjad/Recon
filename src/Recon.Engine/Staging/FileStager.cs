using System.Globalization;
using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine.Parsing;

namespace Recon.Engine.Staging;

/// <summary>
/// Parses one file into <c>stg.StagingTransaction</c> under a run, as a
/// checkpointed step.
///
/// <para>
/// This exists because the pipeline had grown a second copy of itself. The CLI
/// carried the whole sequence — pick the effective format, build the reader
/// and the parser, stream through the bulk loader, persist the rejects, record
/// the step — and the portal needed exactly that sequence to let an operator
/// upload two files and run. Two copies of a pipeline is two pipelines, and
/// they drift; the CLI's own doc comment warned about it. There is one here,
/// and the CLI, the portal and the tests all call it.
/// </para>
///
/// <para>
/// The reader comes from <see cref="RecordReaderFactory"/> rather than being
/// a CSV reader directly, so a dataset configured as XML or JSON loads through
/// the same call. The CLI's copy hard-coded CSV, which quietly made the Phase 7
/// readers unreachable from the only place that loaded files.
/// </para>
/// </summary>
public sealed class FileStager(SqlConnection connection)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>
    /// Stages a file from disk. The path is opened here rather than a stream
    /// being passed in, because a 2M-row file must be read lazily and a caller
    /// holding the stream open across the whole load is the easy way to get
    /// that wrong.
    /// </summary>
    public Task<StageOutcome> StageAsync(
        ReconciliationDefinition definition,
        Side side,
        long runId,
        DateOnly businessDate,
        string path,
        RunRepository runs,
        long? sourceFileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return StageAsync(
            definition.DatasetFor(side), side, runId, businessDate,
            () => File.OpenText(path), runs, sourceFileId, cancellationToken);
    }

    /// <summary>
    /// Stages from any text source. The factory is called once, inside the
    /// step, so a step that is skipped as already completed never opens the
    /// file at all.
    /// </summary>
    public async Task<StageOutcome> StageAsync(
        Dataset dataset,
        Side side,
        long runId,
        DateOnly businessDate,
        Func<TextReader> open,
        RunRepository runs,
        long? sourceFileId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(runs);

        // Parse is a checkpoint per side, so a resumed run skips a file it has
        // already staged instead of doubling 2M rows — and the timing appears
        // on the run page beside the passes.
        var step = await runs.BeginStepAsync(runId, RunStepName.Parse, null, side, cancellationToken)
            .ConfigureAwait(false);

        if (step.AlreadyCompleted)
        {
            return new StageOutcome
            {
                Dataset = dataset.Code,
                Side = side,
                AlreadyStaged = true,
                RowsWritten = 0,
                Elapsed = TimeSpan.Zero,
                Errors = [],
            };
        }

        try
        {
            var config = new ConfigurationRepository(_connection);
            var currencies = await config.LoadCurrenciesAsync(cancellationToken).ConfigureAwait(false);
            var settings = await config.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);

            var format = await FileFormatLoader
                .LoadAsync(_connection, dataset, businessDate, cancellationToken)
                .ConfigureAwait(false);

            // The dataset's declared currency decides the scale every amount
            // is parsed into. A currency the platform does not know is refused
            // rather than defaulted: its minor units are what turn "1.5" into
            // an integer, and guessing them is a factor-of-ten error.
            var currencyCode = dataset.DefaultCurrency ?? "JOD";

            if (!currencies.TryGetValue(currencyCode, out var currency))
            {
                throw new InvalidOperationException(
                    $"dataset {dataset.Code} declares currency '{currencyCode}', which is not in " +
                    "cfg.Currency — its minor units are unknown, so its amounts cannot be scaled.");
            }

            var batchSize = settings.TryGetValue("BulkCopyBatchSize", out var raw)
                && int.TryParse(raw, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 100_000;

            var parser = new RowParser(dataset, format, currency);
            var reader = RecordReaderFactory.For(format);
            var loader = new StagingBulkLoader(
                _connection, new StagingLoadOptions { BatchSize = batchSize });

            var errors = new List<ParseFailure>();

            using var source = open();
            var result = await loader.LoadAsync(Records(), cancellationToken).ConfigureAwait(false);

            // Rejected rows go to stg.ParseError with their line, field,
            // reason and raw line. A count alone is not something an operator
            // can act on.
            if (errors.Count > 0)
            {
                await new ParseErrorWriter(_connection).WriteAsync(
                    runId, businessDate, sourceFileId,
                    errors.Select(e => new ParseErrorRow(
                        e.Type.ToString(), e.FieldCode, e.Message, e.RawRowNumber, e.RawLine))
                        .ToList(),
                    cancellationToken).ConfigureAwait(false);
            }

            await runs.CompleteStepAsync(step.RunStepId, result.RowsWritten, null, null,
                cancellationToken).ConfigureAwait(false);

            return new StageOutcome
            {
                Dataset = dataset.Code,
                Side = side,
                AlreadyStaged = false,
                RowsWritten = result.RowsWritten,
                Elapsed = result.Elapsed,
                Errors = errors,
            };

            IEnumerable<StagingRecord> Records()
            {
                // Every staged row carries the file it came from, so "which
                // file produced this row" is answerable from stg.StagingTransaction
                // rather than from the order the files happened to load in.
                var record = new StagingRecord
                {
                    DatasetId = dataset.DatasetId,
                    LoadRunId = runId,
                    SourceFileId = sourceFileId,
                };

                foreach (var row in reader.Read(source))
                {
                    if (parser.TryParse(row, record, businessDate, out var failure))
                    {
                        yield return record;
                        continue;
                    }

                    errors.Add(failure!);

                    // E4: a wrong-format file would otherwise produce one error
                    // row per source row, each carrying its raw line — a
                    // 2M-row mistake recorded 2M times.
                    if (errors.Count > format.MaxParseErrors)
                    {
                        // The FIRST failure, not the one that tripped the
                        // ceiling: this message said "first problem" while
                        // reporting the 1001st, which sends whoever is
                        // debugging to the wrong line of a file whose real
                        // problem started at the top.
                        var first = errors[0];

                        throw new InvalidOperationException(
                            $"{dataset.Code}: parse errors exceeded MaxParseErrors " +
                            $"({format.MaxParseErrors}), so the file is being treated as the " +
                            $"wrong format. First problem, at line {first.RawRowNumber}: " +
                            $"{first.Message}. The ceiling was reached at line " +
                            $"{failure!.RawRowNumber}.");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException
                                      or NotSupportedException)
        {
            await runs.FailStepAsync(step.RunStepId, ex.Message,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}

public sealed record StageOutcome
{
    public required string Dataset { get; init; }
    public required Side Side { get; init; }

    /// <summary>
    /// True when the run had already staged this side and the step was
    /// skipped. Distinct from "zero rows": a file that staged nothing and a
    /// file that was never opened are different facts.
    /// </summary>
    public required bool AlreadyStaged { get; init; }

    public required long RowsWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<ParseFailure> Errors { get; init; }

    public double RowsPerSecond =>
        Elapsed.TotalSeconds <= 0 ? 0 : RowsWritten / Elapsed.TotalSeconds;
}
