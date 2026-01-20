using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using GcodeHarvestQueueItemStatusDto = Farm.Infrastructure.GcodeHarvestQueueItemStatus;
using GcodeHarvestQueueItemStatusEntity = Farm.Infrastructure.Domain.GcodeHarvestQueueItemStatus;

namespace Farm.Infrastructure.Services.GcodeHarvest;

/// <summary>
/// Database-backed implementation of the gcode harvest queue.
/// Persists queue items to the database for durability across service restarts.
/// </summary>
public class EfGcodeHarvestQueue(AppDbContext db, ILogger<EfGcodeHarvestQueue> logger) : IGcodeHarvestQueue
{
    public async Task<GcodeHarvestQueueItem> EnqueueAsync(Guid printerId, StartGcodeHarvestDto parameters, int priority = 0)
    {
        var queueItem = new GcodeHarvestQueueItem
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            QueuedAt = DateTime.UtcNow,
            Priority = priority,
            Status = (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Pending,
            Parameters = JsonSerializer.Serialize(parameters)
        };

        db.GcodeHarvestQueueItems.Add(queueItem);
        await db.SaveChangesAsync();

        logger.LogInformation("Queued harvest operation for printer {PrinterId}, queue item {QueueItemId}", printerId, queueItem.Id);
        return queueItem;
    }

    public async Task<GcodeHarvestQueueItem?> DequeueAsync()
    {
        // Get the highest priority pending item, oldest first (FIFO within same priority)
        GcodeHarvestQueueItem? nextItem = await db.GcodeHarvestQueueItems
            .Where(q => q.Status == (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Pending)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.QueuedAt)
            .FirstOrDefaultAsync();

        return nextItem;
    }

    public async Task<IReadOnlyList<GcodeHarvestQueueItem>> GetPendingForPrinterAsync(Guid printerId)
    {
        return await db.GcodeHarvestQueueItems
            .Where(q => q.PrinterId == printerId && q.Status == (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Pending)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.QueuedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<GcodeHarvestQueueItem>> GetQueuedItemsAsync(GcodeHarvestQueueItemStatusDto? status = null)
    {
        IQueryable<GcodeHarvestQueueItem> query = db.GcodeHarvestQueueItems.AsQueryable();

        if (status.HasValue)
        {
            query = query.Where(q => q.Status == (GcodeHarvestQueueItemStatusEntity)status.Value);
        }

        return await query
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.QueuedAt)
            .ToListAsync();
    }

    public async Task<bool> CancelAsync(Guid queueItemId)
    {
        GcodeHarvestQueueItem? item = await db.GcodeHarvestQueueItems.FindAsync(queueItemId);
        if (item == null)
        {
            return false;
        }

        // Only allow cancellation of pending items
        if (item.Status != (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Pending)
        {
            logger.LogWarning("Cannot cancel queue item {QueueItemId} - not in pending status: {Status}", queueItemId, item.Status);
            return false;
        }

        item.Status = (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Cancelled;
        item.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Cancelled queue item {QueueItemId}", queueItemId);
        return true;
    }

    public async Task MarkProcessingAsync(Guid queueItemId)
    {
        GcodeHarvestQueueItem? item = await db.GcodeHarvestQueueItems.FindAsync(queueItemId);
        if (item != null)
        {
            item.Status = (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Processing;
            item.ProcessingStartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("Marked queue item {QueueItemId} as processing", queueItemId);
        }
    }

    public async Task MarkCompletedAsync(
        Guid queueItemId,
        int filesFound,
        int filesAdded,
        int filesSkipped,
        int filesErrored)
    {
        GcodeHarvestQueueItem? item = await db.GcodeHarvestQueueItems.FindAsync(queueItemId);
        if (item != null)
        {
            item.Status = (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.FilesFound = filesFound;
            item.FilesAdded = filesAdded;
            item.FilesSkipped = filesSkipped;
            item.FilesErrored = filesErrored;
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Marked queue item {QueueItemId} as completed: {FilesFound} found, {FilesAdded} added",
                queueItemId,
                filesFound,
                filesAdded);
        }
    }

    public async Task MarkFailedAsync(Guid queueItemId, string errorMessage, string? errorDetails = null)
    {
        GcodeHarvestQueueItem? item = await db.GcodeHarvestQueueItems.FindAsync(queueItemId);
        if (item != null)
        {
            item.Status = (GcodeHarvestQueueItemStatusEntity)GcodeHarvestQueueItemStatusDto.Failed;
            item.CompletedAt = DateTime.UtcNow;
            item.ErrorMessage = errorMessage;
            item.ErrorDetails = errorDetails;
            await db.SaveChangesAsync();
            logger.LogError(
                "Marked queue item {QueueItemId} as failed: {ErrorMessage}",
                queueItemId,
                errorMessage);
        }
    }
}
