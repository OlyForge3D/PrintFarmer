namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Scores and ranks candidate printers for a given print job based on
/// material compatibility, hardware fit, queue depth, and user preferences.
/// </summary>
public interface IDispatchScorer
{
    /// <summary>
    /// Evaluates all eligible printers and returns a scored, ranked list
    /// for the specified print job. Eliminated printers are included at
    /// the end of the list with their elimination reasons.
    /// </summary>
    /// <param name="jobId">The print job to find candidates for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scored printers ordered by TotalScore descending, eliminated last.</returns>
    Task<List<DispatchScore>> ScorePrintersForJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Scores a single candidate printer for a job without loading or scoring the rest of the
    /// fleet (issue #1705). Intended for callers — such as the auto-dispatch selection loop —
    /// that only need one printer's result and would otherwise pay for scoring the entire fleet
    /// to read a single entry out of it.
    /// </summary>
    /// <param name="jobId">The print job to score against.</param>
    /// <param name="printerId">The single candidate printer to score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The printer's score, or <see langword="null"/> when the job does not exist, or the
    /// printer does not exist or is not enabled. This must always equal the entry for
    /// <paramref name="printerId"/> in the result of <see cref="ScorePrintersForJobAsync"/> for
    /// the same job — including <see cref="DispatchScore.Eliminated"/> and its elimination
    /// reasons, not just <see cref="DispatchScore.TotalScore"/>. Implementations must preserve
    /// this equivalence.
    /// </returns>
    Task<DispatchScore?> ScorePrinterForJobAsync(Guid jobId, Guid printerId, CancellationToken ct = default);
}

/// <summary>
/// Optional dispatch-scoring capability that carries original input provenance.
/// </summary>
public interface IDispatchScorerWithOrigin : IDispatchScorer
{
    /// <summary>
    /// Evaluates printers and returns the score set with nullable origin provenance.
    /// </summary>
    Task<DispatchScoreResult> ScorePrintersForJobWithOriginAsync(
        Guid jobId,
        CancellationToken ct = default);
}
