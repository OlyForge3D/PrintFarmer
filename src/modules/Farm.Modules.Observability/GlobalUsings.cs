// Global using directives for Farm.Modules.Observability.
// This project uses the plain Microsoft.NET.Sdk (not Sdk.Web), so none of the
// ASP.NET Core / Microsoft.Extensions.* implicit usings that Farm.Web.Api gets
// "for free" apply here. Mirrors the subset of src/api/GlobalUsings.cs that the
// moved observability controllers/services actually need.
global using Farm.Infrastructure;
global using Farm.Infrastructure.Dtos;
global using Farm.Settings;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Extensions.Logging;
