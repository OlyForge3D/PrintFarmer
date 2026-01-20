using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text; // Needed for Encoding when deriving secondary hash
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.Thumbnails;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.FolderManagement;
using Farm.Web.Api.Services.Model;
using Farm.Web.Api.Services.Tags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Request DTO for deleting multiple models
/// </summary>
public sealed class DeleteModelsRequest
{
    [JsonPropertyName("modelIds")]
    public IList<Guid> ModelIds { get; init; } = [];
}
