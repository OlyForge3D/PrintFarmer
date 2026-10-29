namespace Farm.Infrastructure.Services.Catalog;

/// <summary>Names needed by profile-import tasks, without the model's component graphs.</summary>
public sealed record ProfileTaskModelDto(Guid Id, string Name, string ManufacturerName);
