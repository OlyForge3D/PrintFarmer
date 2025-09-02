namespace Farm.Web.Api.Controllers.Requests;

using Farm.Web.Shared;

public record CreateModelRequest(Guid ManufacturerId, string Name, double? MaxX, double? MaxY, double? MaxZ, PrinterBackend? DefaultBackend);
