namespace Farm.Web.Server.Domain;

public class Printer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty; // e.g., http://printer:7125 or PrusaLink base URL (IP-resolved)
    public string? OriginalServerUrl { get; set; } // Original URL/host (for re-resolving if IP changes)
    public string? IpAddress { get; set; } // Last resolved IPv4/IPv6 string for convenience
    public string? Notes { get; set; }

    // Backend type (Moonraker or PrusaLink)
    public int Backend { get; set; } = 0; // 0 = Moonraker, 1 = PrusaLink
    public string? ApiKey { get; set; } // For PrusaLink

    // Metadata
    public Guid? ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public Guid? ModelId { get; set; }
    public PrinterModel? Model { get; set; }
    public DateTime? DateAcquired { get; set; }
}

public class Spool
{
    public Guid Id { get; set; }
    public string Material { get; set; } = string.Empty;
    public double WeightGrams { get; set; }
    public string ColorHex { get; set; } = "#000000";
    public bool InUse { get; set; }
    public Guid? AssignedPrinterId { get; set; }
}

public class Manufacturer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ICollection<PrinterModel> Models { get; set; } = new List<PrinterModel>();
}

public class PrinterModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public double? MaxX { get; set; }
    public double? MaxY { get; set; }
    public double? MaxZ { get; set; }
}
