namespace Farm.Infrastructure.Dtos;

/// <summary>
/// Represents a PowerMonitor as returned by the API.
/// </summary>
public class PowerMonitorDto
{
    public int Id { get; set; }

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Provider type: "Kasa", "Tasmota", "Shelly", or "HomeAssistant".</summary>
    public string Provider { get; set; } = string.Empty;

    public string DeviceAddress { get; set; } = string.Empty;

    public decimal? ElectricityRatePerKwh { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>Request body for creating a new PowerMonitor.</summary>
public class CreatePowerMonitorRequest
{
    public Guid PrinterId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string DeviceAddress { get; set; } = string.Empty;

    public decimal? ElectricityRatePerKwh { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>Request body for updating an existing PowerMonitor.</summary>
public class UpdatePowerMonitorRequest
{
    public Guid PrinterId { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string DeviceAddress { get; set; } = string.Empty;

    public decimal? ElectricityRatePerKwh { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>Request body for the test-connection endpoint.</summary>
public class TestPowerMonitorConnectionRequest
{
    public string Provider { get; set; } = string.Empty;

    public string DeviceAddress { get; set; } = string.Empty;
}

/// <summary>Response from the test-connection endpoint.</summary>
public class TestPowerMonitorConnectionResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public double? CurrentWatts { get; set; }
}
