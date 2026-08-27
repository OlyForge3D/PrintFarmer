// Global using directives for Farm.Modules.Printers.
// This project uses the plain Microsoft.NET.Sdk (not Sdk.Web), so none of the
// ASP.NET Core / Microsoft.Extensions.* implicit usings that Farm.Web.Api gets
// "for free" apply here. Mirrors the subset of src/api/GlobalUsings.cs that the
// moved printers/catalog/discovery controllers and services actually need.
global using Farm.Infrastructure;
global using Farm.Infrastructure.Contracts.Printers.PrusaLink;
global using Farm.Infrastructure.Contracts.SignalR;
global using Farm.Infrastructure.Dtos;
global using Farm.Settings;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
