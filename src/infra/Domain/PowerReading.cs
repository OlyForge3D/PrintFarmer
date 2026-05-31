namespace Farm.Infrastructure.Domain;

public class PowerReading
{
    public long Id { get; set; }

    public int PowerMonitorId { get; set; }

    public PowerMonitor PowerMonitor { get; set; } = null!;

    /// <summary>Current power draw in watts at the moment of recording.</summary>
    public decimal WattsNow { get; set; }

    /// <summary>Accumulated energy in kWh since the device's last reset, if available.</summary>
    public decimal? KwhTotal { get; set; }

    public DateTime RecordedAt { get; set; }
}
