using Microsoft.Data.SqlClient;
using Recon.Data;
using Recon.Domain.Configuration;
using Recon.Engine;

namespace Recon.Web.Services;

/// <summary>
/// The sandbox dry-run (design §2.1, step 6).
///
/// <para>
/// The design is emphatic that this is not optional: without a dry-run,
/// Operations will activate broken rules against production data, and the
/// first time they do it the platform loses its credibility. So the rule
/// builder gets a "dry-run against sample" that reports the match rate per
/// pass <b>before</b> anything goes live.
/// </para>
///
/// <para>
/// A sandbox run writes to the same staging table as any other — that is what
/// makes its numbers trustworthy — but it is <c>RunType = 'Sandbox'</c>, which
/// the unique index excludes from the current-run scope and which period
/// aggregates skip. A nightly job purges them after
/// <c>cfg.PlatformSetting.SandboxPurgeDays</c> (review item E5).
/// </para>
/// </summary>
public sealed class SandboxService(
    SqlConnection connection,
    ConfigurationRepository config,
    RunRepository runs)
{
    private readonly SqlConnection _connection =
        connection ?? throw new ArgumentNullException(nameof(connection));

    private readonly ConfigurationRepository _config =
        config ?? throw new ArgumentNullException(nameof(config));

    private readonly RunRepository _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    /// <summary>
    /// Re-runs the current rule set over a past run's staged rows.
    ///
    /// <para>
    /// This is the honest form of a dry-run for a platform like this. Sampling
    /// a file would tell Operations how the rules behave on rows somebody
    /// chose; replaying real staged data tells them how the rules behave on
    /// what actually arrived. It is a <c>Rematch</c>-shaped operation with the
    /// sandbox flag, so it reads <c>StagingRunId</c> and reuses rows rather
    /// than duplicating 2M of them.
    /// </para>
    /// </summary>
    public async Task<SandboxResult> DryRunAsync(
        int definitionId,
        long sourceRunId,
        string triggeredBy,
        CancellationToken cancellationToken = default)
    {
        var definition = await _config.LoadDefinitionAsync(definitionId, cancellationToken)
            .ConfigureAwait(false);

        var source = await Db.QueryAsync(
            _connection,
            """
            SELECT BusinessDate, SessionRef, StagingRunId, DefinitionId
            FROM ops.ReconRun WHERE RunId = @run;
            """,
            r => (
                BusinessDate: DateOnly.FromDateTime(r.GetDateTime(0)),
                SessionRef: r.IsDBNull(1) ? null : r.GetString(1),
                StagingRunId: r.GetInt64(2),
                DefinitionId: r.GetInt32(3)),
            c => c.With("@run", sourceRunId),
            cancellationToken).ConfigureAwait(false);

        if (source.Count == 0)
        {
            throw new InvalidOperationException($"run {sourceRunId} was not found");
        }

        if (source[0].DefinitionId != definitionId)
        {
            // Replaying one definition's rules over another's staged rows
            // would produce numbers that look real and mean nothing.
            throw new InvalidOperationException(
                $"run {sourceRunId} belongs to definition {source[0].DefinitionId}, not {definitionId}");
        }

        // Activation problems are reported rather than enforced here: a
        // half-built definition is exactly what a dry-run is for.
        var problems = definition.ActivationProblems();

        var run = await _runs.CreateRunAsync(
            definition,
            source[0].BusinessDate,
            source[0].SessionRef,
            RunType.Sandbox,
            triggeredBy,
            sourceRunId: source[0].StagingRunId,
            cancellationToken).ConfigureAwait(false);

        var outcome = await new ReconciliationRunner(_connection, _runs)
            .ExecuteAsync(definition, run, cancellationToken).ConfigureAwait(false);

        return new SandboxResult
        {
            RunId = run.RunId,
            SourceRunId = sourceRunId,
            StagingRunId = run.StagingRunId,
            Outcome = outcome,
            ActivationProblems = problems,
        };
    }

    /// <summary>
    /// Past runs of a definition that have staged rows a dry-run could replay.
    /// A run whose staging has been archived is not offered: the dry-run would
    /// report zero matches and look like a broken rule set.
    /// </summary>
    public Task<List<SandboxSource>> ReplayableRunsAsync(
        int definitionId, CancellationToken cancellationToken = default) =>
        Db.QueryAsync(
            _connection,
            """
            SELECT TOP (20) r.RunId, r.BusinessDate, r.SessionRef, r.RunType, r.Status,
                   (SELECT COUNT_BIG(*) FROM stg.StagingTransaction AS s
                    WHERE s.LoadRunId = r.StagingRunId) AS StagedRows
            FROM ops.ReconRun AS r
            WHERE r.DefinitionId = @def AND r.RunType <> 'Sandbox'
            ORDER BY r.RunId DESC;
            """,
            r => new SandboxSource
            {
                RunId = r.GetInt64(0),
                BusinessDate = DateOnly.FromDateTime(r.GetDateTime(1)),
                SessionRef = r.IsDBNull(2) ? null : r.GetString(2),
                RunType = r.GetString(3),
                Status = r.GetString(4),
                StagedRows = r.GetInt64(5),
            },
            c => c.With("@def", definitionId),
            cancellationToken);
}

public sealed record SandboxResult
{
    public required long RunId { get; init; }
    public required long SourceRunId { get; init; }
    public required long StagingRunId { get; init; }
    public required RunOutcome Outcome { get; init; }
    public required IReadOnlyList<string> ActivationProblems { get; init; }
}

public sealed record SandboxSource
{
    public required long RunId { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public string? SessionRef { get; init; }
    public required string RunType { get; init; }
    public required string Status { get; init; }
    public required long StagedRows { get; init; }

    public bool CanReplay => StagedRows > 0;
}
