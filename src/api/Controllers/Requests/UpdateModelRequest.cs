namespace Farm.Web.Api.Controllers.Requests;

using Farm.Web.Shared;

public record UpdateModelRequest(string Name, PrinterType? Type, double? MaxX, double? MaxY, double? MaxZ, PrinterBackend? DefaultBackend, Guid[]? SupportedFilamentTypeIds);
