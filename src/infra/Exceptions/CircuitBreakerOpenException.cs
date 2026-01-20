using System.Collections.Concurrent;
using Farm.Infrastructure.Telemetry;

namespace Farm.Infrastructure;

public class CircuitBreakerOpenException : Exception
{
    public CircuitBreakerOpenException(string message)
        : base(message)
    {
    }

    public CircuitBreakerOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CircuitBreakerOpenException()
    {
    }
}
