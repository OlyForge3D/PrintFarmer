namespace Farm.Moonraker.Emulator.Domain;

/// <summary>One deterministic Spoolman spool record served through the <c>server/spoolman/proxy</c> fixture.</summary>
public sealed class SpoolmanSpool
{
    public int Id { get; init; }

    public string FilamentName { get; set; } = "Generic PLA";

    public string Material { get; set; } = "PLA";

    public string Color { get; set; } = "#FF0000";

    public double RemainingWeight { get; set; } = 1000.0;

    public double UsedWeight { get; set; }
}

/// <summary>
/// Deterministic per-printer Spoolman fixture backing <c>server/spoolman/status</c>,
/// <c>server/spoolman/spool_id</c>, and the generic <c>server/spoolman/proxy</c> passthrough.
/// </summary>
public sealed class SpoolmanFixture
{
    private readonly object _gate = new();
    private readonly Dictionary<int, SpoolmanSpool> _spools = [];

    public bool Connected { get; set; } = true;

    public int? ActiveSpoolId { get; set; }

    public SpoolmanFixture()
    {
        _spools[1] = new SpoolmanSpool { Id = 1, FilamentName = "Generic PLA", Material = "PLA", Color = "#FF0000" };
        _spools[2] = new SpoolmanSpool { Id = 2, FilamentName = "Generic PETG", Material = "PETG", Color = "#00A0FF" };
        ActiveSpoolId = 1;
    }

    public IReadOnlyList<SpoolmanSpool> Spools()
    {
        lock (_gate)
        {
            return _spools.Values.OrderBy(s => s.Id).ToList();
        }
    }

    public SpoolmanSpool? Find(int id)
    {
        lock (_gate)
        {
            return _spools.GetValueOrDefault(id);
        }
    }

    public void UseFilament(int id, double usedLength, double gramsPerMeter = 3.0)
    {
        lock (_gate)
        {
            if (_spools.TryGetValue(id, out SpoolmanSpool? spool))
            {
                double grams = usedLength * gramsPerMeter / 1000.0;
                spool.UsedWeight += grams;
                spool.RemainingWeight = Math.Max(0, spool.RemainingWeight - grams);
            }
        }
    }
}
