namespace Farm.Web.Api.Services.Workers;

public enum CircuitState
{
    Closed,   // Normal operation
    Open,     // Circuit tripped, worker disabled
    HalfOpen  // Testing if worker recovered
}
