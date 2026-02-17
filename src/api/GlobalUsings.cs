// Global using directives for Farm.Web.Api
global using Farm.Infrastructure;
global using Farm.Infrastructure.Contracts.Auth;
global using Farm.Infrastructure.Contracts.Printers.Moonraker;
global using Farm.Infrastructure.Contracts.Printers.PrusaLink;
global using Farm.Infrastructure.Contracts.Setup;
global using Farm.Infrastructure.Contracts.SignalR;
global using Farm.Infrastructure.Contracts.Workers;

// Slicer module namespaces (types migrated from infra to module)
// NOTE: Farm.Slicer.Module.Services is NOT global — too many adapter bridge collisions
// (IRateLimitService, IStoredFileOperationsService, ITempPathProvider, etc.).
// Import it per-file in slicer-related files only.
global using Farm.Slicer.Module.Contracts;
global using Farm.Slicer.Module.Dtos;
global using Farm.Slicer.Module.Messaging;
global using Farm.Slicer.Module.Models;
