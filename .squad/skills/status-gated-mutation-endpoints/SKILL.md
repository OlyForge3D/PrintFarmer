## Status-Gated Mutation Endpoints

**Pattern:** Reject mutation requests with 409 Conflict when cached printer state forbids the
operation, before attempting any backend I/O.

### When to Use

Apply this pattern to any endpoint that sends commands to a printer (temps, movement, filament
load/unload, etc.) where issuing the command mid-print would be unsafe or meaningless.

### Implementation Checklist

**1. Define busy states (`PrinterControlGate.cs`)**

```csharp
public static class PrinterControlGate
{
    private static readonly HashSet<string> BusyStates =
        new(StringComparer.OrdinalIgnoreCase) { "Printing", "Pausing", "Paused", "Resuming", "Cancelling", "Heating" };

    public static bool IsBusyForControl(string? state)
        => !string.IsNullOrWhiteSpace(state) && BusyStates.Contains(state.Trim());
}
```

Keep this set in sync with `PrintFailureMonitorService` active-print states.

**2. Inject `IPrinterStatusCacheReader` into the controller**

```csharp
public PrintersController(..., IPrinterStatusCacheReader printerStatusCache) { ... }
```

**3. Add `GatePrinterControlAsync` helper**

```csharp
private async Task<ActionResult<CommandResult>?> GatePrinterControlAsync(Guid id, CancellationToken ct)
{
    Printer? printer = await _printersService.FindByIdAsync(id, ct);
    if (printer is null)
        return NotFound(new CommandResult(false, "Printer not found."));

    PrinterStatusDto? status = _printerStatusCache.GetStatus(id);
    if (PrinterControlGate.IsBusyForControl(status?.State))
        return Conflict(new CommandResult(false, $"Printer is currently {status?.State?.ToLowerInvariant()}."));

    return null;
}
```

**4. Call gate at the top of each mutation action**

```csharp
ActionResult<CommandResult>? gate = await GatePrinterControlAsync(id, ct);
if (gate is not null) return gate;
```

**5. Map outcomes from service to HTTP**

```csharp
private ActionResult<CommandResult> MapControlOutcome(PrinterControlOutcome outcome)
    => outcome switch
    {
        PrinterControlOutcome.Ok              => new CommandResult(true, null),
        PrinterControlOutcome.NotFound        => NotFound(new CommandResult(false, "Printer not found.")),
        PrinterControlOutcome.BackendBusy     => StatusCode(502, new CommandResult(false, "Printer firmware refused the command (busy).")),
        PrinterControlOutcome.BackendUnsupported => StatusCode(502, new CommandResult(false, "Backend does not support this command.")),
        _                                     => StatusCode(502, new CommandResult(false, "Command failed.")),
    };
```

### HTTP Code Semantics

| Condition | Code |
|---|---|
| Cached status is busy | 409 Conflict |
| Printer not found | 404 Not Found |
| Firmware refused (plugin-side 409) | 502 Bad Gateway |
| Backend unsupported / unreachable | 502 Bad Gateway |

**Key rule:** 409 = API pre-flight says "don't even try." 502 = we tried, upstream said no.

### Plugin-Layer 409 Propagation

When a backend plugin receives HTTP 409 from firmware:

```csharp
if (response.StatusCode == HttpStatusCode.Conflict)
    throw new PrinterBackendBusyException($"Firmware refused at {baseUrl}.");
```

The service layer catches this and maps to `PrinterControlOutcome.BackendBusy`, which the
controller maps to 502. This lets clients distinguish "asked at wrong time" (409) from "firmware
blocked it" (502).

### Test Pattern

```csharp
[Fact]
public async Task SetTempsAsync_ReturnsConflict_WhenPrinterIsPrinting()
{
    var printersService = new Mock<IPrintersService>();
    printersService.Setup(s => s.FindByIdAsync(id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(SamplePrinter(id));

    var statusCache = new Mock<IPrinterStatusCacheReader>();
    statusCache.Setup(c => c.GetStatus(id))
        .Returns(new PrinterStatusDto(id, IsOnline: true, State: "Printing"));

    var result = await controller.SetTempsAsync(id, targets, CancellationToken.None);

    var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
    var body = Assert.IsType<CommandResult>(conflict.Value);
    Assert.False(body.Success);
    // Verify downstream service was never called
    printersService.Verify(s => s.SetTempsAsync(...), Times.Never);
}
```

### Reference Implementation

- `src/infra/Services/Printers/PrinterControlGate.cs`
- `src/infra/Services/Printers/PrinterControlOutcome.cs`
- `src/infra/Services/Printers/PrinterBackendBusyException.cs`
- `src/api/Controllers/PrintersController.cs` (GatePrinterControlAsync + MapControlOutcome)
- `src/tests/Farm.Web.Api.Tests/Controllers/PrintersControllerControlGuardsTests.cs`
