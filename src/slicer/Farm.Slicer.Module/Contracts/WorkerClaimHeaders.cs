namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Header names used to bind worker requests to one specific job claim.
/// </summary>
public static class WorkerClaimHeaders
{
    /// <summary>
    /// Identifies the claim incarnation authorizing a worker operation.
    /// </summary>
    public const string ClaimToken = "X-Claim-Token";
}
