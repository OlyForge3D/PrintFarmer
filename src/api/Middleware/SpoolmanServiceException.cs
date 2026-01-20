using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Custom exception for Spoolman service errors
/// </summary>
public class SpoolmanServiceException : Exception
{
    public SpoolmanServiceException(string message) : base(message)
    {
    }

    public SpoolmanServiceException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public SpoolmanServiceException()
    {
    }
}
