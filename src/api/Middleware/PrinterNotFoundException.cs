using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Middleware;

/// <summary>
/// Custom exception for printer not found scenarios
/// </summary>
public class PrinterNotFoundException : Exception
{
    public PrinterNotFoundException(string message) : base(message)
    {
    }

    public PrinterNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public PrinterNotFoundException()
    {
    }
}
