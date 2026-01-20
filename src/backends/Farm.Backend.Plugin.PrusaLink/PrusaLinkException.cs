#pragma warning disable S1006, CS1998, S1939 // Default parameters, async methods, and explicit interface inheritance are intentional

using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers.PrusaLink;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;

namespace Farm.Backend.Plugin.PrusaLink;

public class PrusaLinkException : Exception
{
    public PrusaLinkError? ErrorDetails { get; }

    public int StatusCode { get; }

    public PrusaLinkException(string message) : base(message)
    {
    }

    public PrusaLinkException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public PrusaLinkException(string message, int statusCode, PrusaLinkError? errorDetails = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorDetails = errorDetails;
    }

    public PrusaLinkException()
    {
    }
}

#pragma warning restore CS1066
