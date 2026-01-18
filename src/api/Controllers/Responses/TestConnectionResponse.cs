namespace Farm.Web.Api.Controllers.Responses;

/// <summary>
/// Response from testing connectivity to a printer backend.
/// </summary>
public class TestConnectionResponse
{
    /// <summary>
    /// Whether the connection test was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable message about the connection test result.
    /// </summary>
    public string? Message { get; set; }
}
