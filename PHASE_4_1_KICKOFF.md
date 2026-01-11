# Phase 4.1: Auto-Enqueue from File Uploads - DEFERRED TO PHASE 5

**Phase**: 4.1 - Auto-Enqueue from File Uploads (NOW PHASE 5)
**Status**: ⏸️ DEFERRED (January 11, 2026)
**Original Estimate**: 2 days (January 13-14, 2026)
**New Timeline**: Post-Phase 4 completion (~1-2 weeks)
**Priority**: P2 - Core automation feature (deferred for Phase 4 scheduling focus)

---

## Overview

This phase has been **moved to Phase 5 (Future)** to prioritize job scheduling in Phase 4. Auto-enqueue functionality will be implemented after Phase 4.5 (Load Balancing) is complete.

**Rationale for Deferral**:
- Job scheduling (Phase 4.1 new) provides immediate user value
- Scheduling is a prerequisite for advanced automation features
- Auto-enqueue can be added after core scheduling is stable
- Allows focus on predictive estimates and notifications first

---

## Deferred Content

The complete implementation specification for auto-enqueue from file uploads is preserved below for Phase 5 implementation.

---

## Implementation Tasks (FOR PHASE 5)

### Task 4.1.1: Backend Models & Database (Day 1, Morning)

**Files to Create**:
1. `src/infra/Models/AutoEnqueueSettings.cs` - Settings model
2. `src/infra/Models/PrinterAutoEnqueueConfig.cs` - Per-printer config

**Models**:

```csharp
/// <summary>
/// Global auto-enqueue settings
/// </summary>
public class AutoEnqueueSettings
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Is auto-enqueue globally enabled
    /// </summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>
    /// Default material when not specified
    /// </summary>
    public string DefaultMaterial { get; set; } = "PLA";

    /// <summary>
    /// Default job priority
    /// </summary>
    public int DefaultPriority { get; set; } = 0;

    /// <summary>
    /// Default printer ID (null = auto-select)
    /// </summary>
    public string? DefaultPrinterId { get; set; }

    /// <summary>
    /// Enable load balancing when selecting printer
    /// </summary>
    public bool UseLoadBalancing { get; set; } = true;

    /// <summary>
    /// Maximum concurrent jobs per printer
    /// </summary>
    public int MaxConcurrentPerPrinter { get; set; } = 3;

    /// <summary>
    /// Skip modal and enqueue immediately
    /// </summary>
    public bool SkipEnqueueModal { get; set; } = true;

    /// <summary>
    /// Notify user on auto-enqueue
    /// </summary>
    public bool NotifyOnEnqueue { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ConcurrencyCheck]
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Per-printer auto-enqueue configuration
/// </summary>
public class PrinterAutoEnqueueConfig
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [ForeignKey(nameof(Printer))]
    public string PrinterId { get; set; }
    public virtual Printer Printer { get; set; }

    /// <summary>
    /// Is auto-enqueue enabled for this printer
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Maximum jobs to queue for this printer
    /// </summary>
    public int MaxQueueDepth { get; set; } = 5;

    /// <summary>
    /// Preferred material for this printer
    /// </summary>
    public string? PreferredMaterial { get; set; }

    /// <summary>
    /// Only accept specific models (comma-separated IDs, null = all)
    /// </summary>
    public string? AllowedModelIds { get; set; }

    /// <summary>
    /// Only accept specific materials (comma-separated, null = all)
    /// </summary>
    public string? AllowedMaterials { get; set; }

    /// <summary>
    /// Priority boost for this printer (higher = prefer)
    /// </summary>
    public int PriorityBoost { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

**Database Configuration** (in `AppDbContext.cs`):

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    // ... existing configuration ...

    // AutoEnqueueSettings - Singleton pattern (one row)
    modelBuilder.Entity<AutoEnqueueSettings>()
        .HasKey(x => x.Id);
    modelBuilder.Entity<AutoEnqueueSettings>()
        .Property(x => x.RowVersion)
        .IsRowVersion();
    modelBuilder.Entity<AutoEnqueueSettings>()
        .HasIndex(x => x.CreatedAt);

    // PrinterAutoEnqueueConfig
    modelBuilder.Entity<PrinterAutoEnqueueConfig>()
        .HasKey(x => x.Id);
    modelBuilder.Entity<PrinterAutoEnqueueConfig>()
        .HasOne(x => x.Printer)
        .WithMany()
        .HasForeignKey(x => x.PrinterId)
        .OnDelete(DeleteBehavior.Cascade);
    modelBuilder.Entity<PrinterAutoEnqueueConfig>()
        .HasIndex(x => x.PrinterId)
        .IsUnique();

    // Seed default settings
    modelBuilder.Entity<AutoEnqueueSettings>()
        .HasData(new AutoEnqueueSettings
        {
            Id = "default-auto-enqueue",
            IsEnabled = false,
            DefaultMaterial = "PLA",
            DefaultPriority = 0,
            UseLoadBalancing = true,
            MaxConcurrentPerPrinter = 3,
            SkipEnqueueModal = true,
            NotifyOnEnqueue = true,
            CreatedAt = new DateTime(2026, 1, 11, 0, 0, 0, DateTimeKind.Utc)
        });
}
```

**Migration**:
```bash
cd /home/pi/pfarm/src
dotnet ef migrations add AddAutoEnqueueSettings --project infra --startup-project api
dotnet ef database update --project api
```

---

### Task 4.1.2: Service Layer (Day 1, Afternoon)

**File**: `src/api/Services/AutoEnqueueService.cs`

```csharp
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.AutoEnqueue;

public interface IAutoEnqueueService
{
    Task<AutoEnqueueSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(UpdateAutoEnqueueSettingsDto settings, CancellationToken cancellationToken = default);
    Task<PrintJobDto> AutoEnqueueFileAsync(string fileId, string? printerId = null, CancellationToken cancellationToken = default);
    Task<Printer> SelectOptimalPrinterAsync(string? fileId = null, CancellationToken cancellationToken = default);
}

public class AutoEnqueueService : IAutoEnqueueService
{
    private readonly AppDbContext _context;
    private readonly IUnifiedLoggingService _logger;
    private readonly IPrintQueueService _printQueueService;

    public AutoEnqueueService(
        AppDbContext context,
        IUnifiedLoggingService logger,
        IPrintQueueService printQueueService)
    {
        _context = context;
        _logger = logger;
        _printQueueService = printQueueService;
    }

    public async Task<AutoEnqueueSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _context.Set<AutoEnqueueSettings>()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Auto-enqueue settings not found");

        return new AutoEnqueueSettingsDto
        {
            IsEnabled = settings.IsEnabled,
            DefaultMaterial = settings.DefaultMaterial,
            DefaultPriority = settings.DefaultPriority,
            DefaultPrinterId = settings.DefaultPrinterId,
            UseLoadBalancing = settings.UseLoadBalancing,
            MaxConcurrentPerPrinter = settings.MaxConcurrentPerPrinter,
            SkipEnqueueModal = settings.SkipEnqueueModal,
            NotifyOnEnqueue = settings.NotifyOnEnqueue
        };
    }

    public async Task UpdateSettingsAsync(
        UpdateAutoEnqueueSettingsDto dto,
        CancellationToken cancellationToken = default)
    {
        var settings = await _context.Set<AutoEnqueueSettings>()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Auto-enqueue settings not found");

        settings.IsEnabled = dto.IsEnabled;
        settings.DefaultMaterial = dto.DefaultMaterial ?? settings.DefaultMaterial;
        settings.DefaultPriority = dto.DefaultPriority ?? settings.DefaultPriority;
        settings.DefaultPrinterId = dto.DefaultPrinterId;
        settings.UseLoadBalancing = dto.UseLoadBalancing ?? settings.UseLoadBalancing;
        settings.MaxConcurrentPerPrinter = dto.MaxConcurrentPerPrinter ?? settings.MaxConcurrentPerPrinter;
        settings.SkipEnqueueModal = dto.SkipEnqueueModal ?? settings.SkipEnqueueModal;
        settings.NotifyOnEnqueue = dto.NotifyOnEnqueue ?? settings.NotifyOnEnqueue;
        settings.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("[AutoEnqueue] Settings updated");
    }

    public async Task<PrintJobDto> AutoEnqueueFileAsync(
        string fileId,
        string? printerId = null,
        CancellationToken cancellationToken = default)
    {
        // Get settings
        var settings = await GetSettingsAsync(cancellationToken);
        if (!settings.IsEnabled)
            throw new InvalidOperationException("Auto-enqueue is not enabled");

        // Get file
        var file = await _context.Set<GcodeFile>()
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken)
            ?? throw new InvalidOperationException("File not found");

        // Select printer if not specified
        printerId ??= settings.DefaultPrinterId;
        if (string.IsNullOrEmpty(printerId))
        {
            var printer = await SelectOptimalPrinterAsync(fileId, cancellationToken);
            printerId = printer.Id;
        }

        // Validate printer exists and is available
        var printerEntity = await _context.Printers
            .FirstOrDefaultAsync(p => p.Id == printerId, cancellationToken)
            ?? throw new InvalidOperationException("Printer not found");

        if (printerEntity.Status != "Online")
            throw new InvalidOperationException("Printer is not online");

        // Create job
        var job = new PrintJob
        {
            Id = Guid.NewGuid().ToString(),
            PrinterId = printerId,
            GcodeFileId = fileId,
            Status = "Queued",
            Priority = settings.DefaultPriority,
            Name = file.Name,
            Material = settings.DefaultMaterial,
            Notes = "Auto-enqueued by system",
            EnqueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _context.PrintJobs.Add(job);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation($"[AutoEnqueue] File '{file.Name}' auto-enqueued to printer '{printerEntity.Name}'");

        return await _printQueueService.GetJobAsync(job.Id, cancellationToken);
    }

    public async Task<Printer> SelectOptimalPrinterAsync(
        string? fileId = null,
        CancellationToken cancellationToken = default)
    {
        // Get all online printers
        var printers = await _context.Printers
            .Where(p => p.Status == "Online")
            .ToListAsync(cancellationToken);

        if (!printers.Any())
            throw new InvalidOperationException("No online printers available");

        // Get queue depth for each printer
        var queueDepths = await _context.PrintJobs
            .Where(j => j.Status == "Queued" || j.Status == "Printing")
            .GroupBy(j => j.PrinterId)
            .Select(g => new { PrinterId = g.Key, Depth = g.Count() })
            .ToDictionaryAsync(x => x.PrinterId, x => x.Depth, cancellationToken);

        // Select printer with lowest queue depth
        var optimalPrinter = printers
            .OrderBy(p => queueDepths.GetValueOrDefault(p.Id, 0))
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No suitable printer found");

        _logger.LogInformation($"[AutoEnqueue] Selected printer '{optimalPrinter.Name}' (queue depth: {queueDepths.GetValueOrDefault(optimalPrinter.Id, 0)})");

        return optimalPrinter;
    }
}
```

**DTOs** (add to `src/api/DTOs/PrintQueueDtos.cs`):

```csharp
public class AutoEnqueueSettingsDto
{
    public bool IsEnabled { get; set; }
    public string DefaultMaterial { get; set; }
    public int DefaultPriority { get; set; }
    public string? DefaultPrinterId { get; set; }
    public bool UseLoadBalancing { get; set; }
    public int MaxConcurrentPerPrinter { get; set; }
    public bool SkipEnqueueModal { get; set; }
    public bool NotifyOnEnqueue { get; set; }
}

public class UpdateAutoEnqueueSettingsDto
{
    public bool IsEnabled { get; set; }
    public string? DefaultMaterial { get; set; }
    public int? DefaultPriority { get; set; }
    public string? DefaultPrinterId { get; set; }
    public bool? UseLoadBalancing { get; set; }
    public int? MaxConcurrentPerPrinter { get; set; }
    public bool? SkipEnqueueModal { get; set; }
    public bool? NotifyOnEnqueue { get; set; }
}
```

---

### Task 4.1.3: Controller (Day 1, Afternoon)

**File**: `src/api/Controllers/AutoEnqueueController.cs`

```csharp
using Farm.Web.Api.Services.AutoEnqueue;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AutoEnqueueController : ControllerBase
{
    private readonly IAutoEnqueueService _autoEnqueueService;
    private readonly ILogger<AutoEnqueueController> _logger;

    public AutoEnqueueController(
        IAutoEnqueueService autoEnqueueService,
        ILogger<AutoEnqueueController> logger)
    {
        _autoEnqueueService = autoEnqueueService;
        _logger = logger;
    }

    /// <summary>
    /// Get current auto-enqueue settings
    /// </summary>
    [HttpGet("settings")]
    public async Task<ActionResult<AutoEnqueueSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _autoEnqueueService.GetSettingsAsync(cancellationToken);
            return Ok(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get auto-enqueue settings");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Update auto-enqueue settings
    /// </summary>
    [HttpPut("settings")]
    public async Task<ActionResult<AutoEnqueueSettingsDto>> UpdateSettings(
        [FromBody] UpdateAutoEnqueueSettingsDto dto,
        CancellationToken cancellationToken)
    {
        try
        {
            await _autoEnqueueService.UpdateSettingsAsync(dto, cancellationToken);
            var updated = await _autoEnqueueService.GetSettingsAsync(cancellationToken);
            return Ok(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update auto-enqueue settings");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Auto-enqueue a file
    /// </summary>
    [HttpPost("files/{fileId}")]
    public async Task<ActionResult<PrintJobDto>> AutoEnqueueFile(
        string fileId,
        [FromQuery] string? printerId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var job = await _autoEnqueueService.AutoEnqueueFileAsync(fileId, printerId, cancellationToken);
            return CreatedAtAction(nameof(AutoEnqueueFile), new { fileId }, job);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Auto-enqueue validation failed");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-enqueue file");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
```

---

### Task 4.1.4: Frontend Component (Day 2, Morning)

**File**: `src/Web/ReactApp/src/features/queue/components/AutoEnqueueSettings.tsx`

```typescript
import React, { useEffect, useState } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { Card } from '@/common/components/ui/Card';
import { Button } from '@/common/components/ui/Button';
import { Toggle } from '@/common/components/ui/Toggle';
import { Select } from '@/common/components/ui/Select';
import { Input } from '@/common/components/ui/Input';
import { Alert } from '@/common/components/ui/Alert';
import { usePrinters } from '@/hooks/usePrinters';
import { autoEnqueueService } from '@/services/autoEnqueueService';

export interface AutoEnqueueSettingsProps {
  onSettingsSaved?: () => void;
}

export const AutoEnqueueSettings: React.FC<AutoEnqueueSettingsProps> = ({ onSettingsSaved }) => {
  const [isSaving, setIsSaving] = useState(false);
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  const { data: printers } = usePrinters();

  const { data: settings, isLoading, refetch } = useQuery({
    queryKey: ['autoEnqueueSettings'],
    queryFn: () => autoEnqueueService.getSettings(),
  });

  const [formData, setFormData] = useState<typeof settings>(settings);

  useEffect(() => {
    if (settings) {
      setFormData(settings);
    }
  }, [settings]);

  const handleSave = async () => {
    if (!formData) return;
    setIsSaving(true);
    setSaveError(null);
    setSaveMessage(null);

    try {
      await autoEnqueueService.updateSettings(formData);
      setSaveMessage('Settings saved successfully');
      refetch();
      onSettingsSaved?.();
    } catch (error) {
      setSaveError(error instanceof Error ? error.message : 'Failed to save settings');
    } finally {
      setIsSaving(false);
    }
  };

  if (isLoading || !formData) {
    return <div>Loading...</div>;
  }

  const printerOptions = [
    { value: '', label: 'Auto-select (Load Balance)' },
    ...(printers?.map(p => ({ value: p.id, label: p.name })) ?? []),
  ];

  return (
    <Card className="p-6">
      <h3 className="text-lg font-semibold mb-6">Auto-Enqueue Settings</h3>

      {saveMessage && (
        <Alert variant="success" className="mb-4">
          {saveMessage}
        </Alert>
      )}

      {saveError && (
        <Alert variant="error" className="mb-4">
          {saveError}
        </Alert>
      )}

      <div className="space-y-6">
        {/* Enable/Disable */}
        <div className="flex items-center justify-between">
          <div>
            <label className="text-sm font-medium">Enable Auto-Enqueue</label>
            <p className="text-xs text-gray-600">
              Automatically enqueue files when uploaded
            </p>
          </div>
          <Toggle
            checked={formData.isEnabled}
            onChange={(checked) =>
              setFormData({ ...formData, isEnabled: checked })
            }
            disabled={isSaving}
          />
        </div>

        {formData.isEnabled && (
          <>
            {/* Default Material */}
            <div>
              <label className="block text-sm font-medium mb-2">
                Default Material
              </label>
              <Select
                value={formData.defaultMaterial}
                onChange={(value) =>
                  setFormData({ ...formData, defaultMaterial: value })
                }
                disabled={isSaving}
                options={[
                  { value: 'PLA', label: 'PLA' },
                  { value: 'PETG', label: 'PETG' },
                  { value: 'ABS', label: 'ABS' },
                  { value: 'TPU', label: 'TPU' },
                  { value: 'Nylon', label: 'Nylon' },
                ]}
              />
            </div>

            {/* Default Priority */}
            <div>
              <label className="block text-sm font-medium mb-2">
                Default Priority
              </label>
              <Input
                type="number"
                value={formData.defaultPriority}
                onChange={(e) =>
                  setFormData({ ...formData, defaultPriority: parseInt(e.target.value, 10) })
                }
                disabled={isSaving}
                min="-10"
                max="10"
              />
              <p className="text-xs text-gray-600">-10 (lowest) to 10 (highest)</p>
            </div>

            {/* Default Printer */}
            <div>
              <label className="block text-sm font-medium mb-2">
                Default Printer
              </label>
              <Select
                value={formData.defaultPrinterId || ''}
                onChange={(value) =>
                  setFormData({ ...formData, defaultPrinterId: value || null })
                }
                disabled={isSaving}
                options={printerOptions}
              />
            </div>

            {/* Load Balancing */}
            <div className="flex items-center justify-between">
              <div>
                <label className="text-sm font-medium">Enable Load Balancing</label>
                <p className="text-xs text-gray-600">
                  Distribute jobs across printers based on queue depth
                </p>
              </div>
              <Toggle
                checked={formData.useLoadBalancing}
                onChange={(checked) =>
                  setFormData({ ...formData, useLoadBalancing: checked })
                }
                disabled={isSaving}
              />
            </div>

            {/* Max Concurrent Jobs */}
            {formData.useLoadBalancing && (
              <div>
                <label className="block text-sm font-medium mb-2">
                  Max Concurrent Jobs Per Printer
                </label>
                <Input
                  type="number"
                  value={formData.maxConcurrentPerPrinter}
                  onChange={(e) =>
                    setFormData({
                      ...formData,
                      maxConcurrentPerPrinter: parseInt(e.target.value, 10),
                    })
                  }
                  disabled={isSaving}
                  min="1"
                  max="10"
                />
              </div>
            )}

            {/* Skip Modal */}
            <div className="flex items-center justify-between">
              <div>
                <label className="text-sm font-medium">Skip Enqueue Modal</label>
                <p className="text-xs text-gray-600">
                  Go straight to printing without confirmation
                </p>
              </div>
              <Toggle
                checked={formData.skipEnqueueModal}
                onChange={(checked) =>
                  setFormData({ ...formData, skipEnqueueModal: checked })
                }
                disabled={isSaving}
              />
            </div>

            {/* Notify on Enqueue */}
            <div className="flex items-center justify-between">
              <div>
                <label className="text-sm font-medium">Notify on Enqueue</label>
                <p className="text-xs text-gray-600">
                  Show notification when file is auto-enqueued
                </p>
              </div>
              <Toggle
                checked={formData.notifyOnEnqueue}
                onChange={(checked) =>
                  setFormData({ ...formData, notifyOnEnqueue: checked })
                }
                disabled={isSaving}
              />
            </div>
          </>
        )}
      </div>

      {/* Save Button */}
      <div className="mt-8 flex gap-4">
        <Button
          onClick={handleSave}
          isLoading={isSaving}
          disabled={isSaving}
          variant="primary"
        >
          Save Settings
        </Button>
      </div>
    </Card>
  );
};

export default AutoEnqueueSettings;
```

**Service** (create `src/services/autoEnqueueService.ts`):

```typescript
import { apiClient } from './apiClient';

export interface AutoEnqueueSettingsDto {
  isEnabled: boolean;
  defaultMaterial: string;
  defaultPriority: number;
  defaultPrinterId: string | null;
  useLoadBalancing: boolean;
  maxConcurrentPerPrinter: number;
  skipEnqueueModal: boolean;
  notifyOnEnqueue: boolean;
}

export const autoEnqueueService = {
  async getSettings(): Promise<AutoEnqueueSettingsDto> {
    const response = await apiClient.get<AutoEnqueueSettingsDto>('/autoEnqueue/settings');
    return response.data;
  },

  async updateSettings(settings: Partial<AutoEnqueueSettingsDto>): Promise<AutoEnqueueSettingsDto> {
    const response = await apiClient.put<AutoEnqueueSettingsDto>('/autoEnqueue/settings', settings);
    return response.data;
  },

  async autoEnqueueFile(fileId: string, printerId?: string): Promise<any> {
    const response = await apiClient.post(`/autoEnqueue/files/${fileId}`, {
      params: printerId ? { printerId } : undefined,
    });
    return response.data;
  },
};
```

---

### Task 4.1.5: Register Services & Update Program.cs (Day 2, Afternoon)

**Update** `src/api/Program.cs`:

```csharp
// Add auto-enqueue services
services.AddScoped<IAutoEnqueueService, AutoEnqueueService>();
```

---

### Task 4.1.6: Tests (Day 2, Afternoon)

**File**: `src/tests/Farm.Web.Api.Tests/Services/AutoEnqueueServiceTests.cs`

```csharp
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.AutoEnqueue;
using Xunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.Services;

public class AutoEnqueueServiceTests
{
    private readonly AppDbContext _context;
    private readonly AutoEnqueueService _service;

    public AutoEnqueueServiceTests()
    {
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _context.Database.EnsureCreated();
        _service = new AutoEnqueueService(_context, new MockLogger(), new MockPrintQueueService());
    }

    [Fact]
    public async Task GetSettings_ReturnsDefaultSettings()
    {
        // Act
        var settings = await _service.GetSettingsAsync();

        // Assert
        settings.Should().NotBeNull();
        settings.IsEnabled.Should().BeFalse();
        settings.DefaultMaterial.Should().Be("PLA");
    }

    [Fact]
    public async Task UpdateSettings_UpdatesValuesCorrectly()
    {
        // Arrange
        var updateDto = new UpdateAutoEnqueueSettingsDto
        {
            IsEnabled = true,
            DefaultMaterial = "PETG",
            DefaultPriority = 5,
        };

        // Act
        await _service.UpdateSettingsAsync(updateDto);
        var updated = await _service.GetSettingsAsync();

        // Assert
        updated.IsEnabled.Should().BeTrue();
        updated.DefaultMaterial.Should().Be("PETG");
        updated.DefaultPriority.Should().Be(5);
    }

    [Fact]
    public async Task SelectOptimalPrinter_ChoosesLowestQueueDepth()
    {
        // Arrange - Create 3 online printers with different queue depths
        var printer1 = new Printer { Id = "p1", Name = "Printer 1", Status = "Online" };
        var printer2 = new Printer { Id = "p2", Name = "Printer 2", Status = "Online" };
        var printer3 = new Printer { Id = "p3", Name = "Printer 3", Status = "Online" };

        _context.Printers.AddRange(printer1, printer2, printer3);

        // Add jobs to queue for p1 (2 jobs) and p2 (1 job), p3 (0 jobs)
        _context.PrintJobs.AddRange(
            new PrintJob { Id = "j1", PrinterId = "p1", Status = "Queued" },
            new PrintJob { Id = "j2", PrinterId = "p1", Status = "Queued" },
            new PrintJob { Id = "j3", PrinterId = "p2", Status = "Queued" }
        );

        await _context.SaveChangesAsync();

        // Act
        var selected = await _service.SelectOptimalPrinterAsync();

        // Assert
        selected.Id.Should().Be("p3"); // Has lowest queue depth (0)
    }
}
```

---

## Validation Checklist

- ✅ Models created with proper relationships
- ✅ Database migration runs without errors
- ✅ Service methods implemented
- ✅ Controller endpoints functional
- ✅ React component renders correctly
- ✅ Settings persisting to database
- ✅ Auto-enqueue triggering on file upload
- ✅ Printer selection algorithm working
- ✅ Tests passing (95%+)
- ✅ 0 build warnings/errors
- ✅ TypeScript compilation clean
- ✅ ESLint passing

---

## Success Criteria

By end of Phase 4.1:
- ✅ Auto-enqueue feature fully implemented
- ✅ Settings UI working
- ✅ File upload triggers auto-enqueue
- ✅ Tests passing
- ✅ Ready for Phase 4.2 (Scheduling)

---

## Next Steps

After Phase 4.1 completion:
1. Review test results
2. Deploy to staging
3. Manual QA testing
4. Begin Phase 4.2 (Job Scheduling)

---

*Phase 4.1 - Auto-Enqueue from File Uploads*  
*KICKOFF - January 11, 2026*
