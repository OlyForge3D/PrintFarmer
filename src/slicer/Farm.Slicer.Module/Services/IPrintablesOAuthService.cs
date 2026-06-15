using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Handles Printables OAuth2 account linking and guarded authenticated access helpers.
/// </summary>
public interface IPrintablesOAuthService
{
    Task<PrintablesOAuthConnectResponseDto> BuildConnectUrlAsync(Guid userId, CancellationToken ct);

    Task<PrintablesOAuthStatusDto> HandleCallbackAsync(Guid userId, string code, string state, CancellationToken ct);

    Task<PrintablesOAuthStatusDto> GetStatusAsync(Guid userId, CancellationToken ct);

    Task DisconnectAsync(Guid userId, CancellationToken ct);

    Task<PrintablesAuthenticatedCursorPageDto> GetLikedModelsAsync(Guid userId, int limit, string? cursor, CancellationToken ct);

    Task<PrintablesAuthenticatedCursorPageDto> GetDownloadHistoryAsync(Guid userId, int limit, string? cursor, CancellationToken ct);
}
