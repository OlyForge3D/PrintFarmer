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
}
