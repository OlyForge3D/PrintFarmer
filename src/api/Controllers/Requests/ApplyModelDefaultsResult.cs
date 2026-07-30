namespace Farm.Web.Api.Controllers.Requests;

/// <summary>
/// Result of bulk-applying model dispatch defaults to existing printers.
/// </summary>
public record ApplyModelDefaultsResult(int UpdatedCount, int SkippedCount);
