# Network Discovery Cancellation Token Design

## Problem: Background Task Cancellation

When starting a background discovery task from an HTTP endpoint, we need to:
1. Return the response immediately to the client
2. Allow the discovery to run in the background independent of the request lifecycle
3. Allow users to cancel discovery on demand

## The Challenge with Linked Tokens

Initially, a **linked cancellation token** approach was attempted:

```csharp
// ❌ INCORRECT: Passes request's CancellationToken
_ = Task.Run(async () =>
{
    using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    linkedCts.CancelAfter(TimeSpan.FromMinutes(15));
    await discoveryService.DiscoverAsync(linkedCts.Token);
});
```

### Why This Doesn't Work

A **linked cancellation token** combines multiple cancellation sources:

```
Request CancellationToken ──┐
                            ├──> Linked Token
15-Minute Timeout ──────────┘
```

**Problem**: When the HTTP response is sent (which happens immediately after returning `Ok(...)`), the request's `CancellationToken` is **disposed**. This causes the linked token to be cancelled, terminating the background task prematurely.

## Solution: Independent CancellationTokenSource with Cache-Based Cancellation

Instead of linking to the request token, we create an **independent** `CancellationTokenSource` and store it in the progress cache:

### How It Works

**1. Start Discovery Endpoint (`POST /api/printers/discover/stream`)**
```csharp
_ = Task.Run(async () =>
{
    CancellationTokenSource discoveryCts = new();  // ✅ NEW, independent token
    try
    {
        // Store the CTS so clients can request cancellation
        discoveryProgressCache.SetCancellationSource(sessionId, discoveryCts);
        
        // Hard timeout after 15 minutes
        discoveryCts.CancelAfter(TimeSpan.FromMinutes(15));
        
        await networkDiscovery.DiscoverPrintersWithProgressAsync(
            sessionId, 
            backends, 
            discoveryCts.Token);
    }
    finally
    {
        discoveryProgressCache.Remove(sessionId);
        discoveryCts.Dispose();
    }
});
```

**2. Cancel Discovery Endpoint (`POST /api/printers/discover/{sessionId}/cancel`)**
```csharp
[HttpPost("discover/{sessionId}/cancel")]
public async Task<IActionResult> CancelDiscoveryAsync(string sessionId)
{
    // Retrieves the stored CancellationTokenSource and cancels it
    bool cancelled = discoveryProgressCache.TryCancel(sessionId);
    
    return cancelled 
        ? Ok(new { message = "Cancellation requested" })
        : NotFound(new { error = "Session not found" });
}
```

### Key Benefits

✅ **Independent Lifecycle**: Discovery runs independent of request lifecycle
✅ **User-Requested Cancellation**: Users can cancel via the cancel endpoint
✅ **Hard Timeout**: 15-minute timeout prevents runaway discovery
✅ **Clean Cleanup**: CancellationTokenSource is disposed in finally block
✅ **Cache-Based**: Progress cache doubles as token storage for multi-threaded safety

## Linked Token Patterns (When They ARE Appropriate)

Linked tokens ARE useful in other scenarios:

### ✅ Lease Renewal Loop (HttpJobPollerService)
```csharp
CancellationTokenSource localLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
// ✅ Works here because:
// - The loop owns and disposes the CTS in its finally block
// - When request's ct is cancelled, the linked token immediately notices
// - No need for external cancellation mechanism
```

### ✅ Long-Running Service Operations
```csharp
// In a hosted service
CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serviceLifetimeToken);
// ✅ Works because service lifetime token lives as long as the service
```

## Summary

| Pattern | Use Case | Why |
|---------|----------|-----|
| **Independent CTS** (Discovery) | Long-running tasks with user-requested cancellation | Request token gets disposed; need separate lifetime management |
| **Linked CTS** (Lease Renewal) | Short-term tasks that should inherit parent cancellation | Ensures immediate propagation of parent's cancellation |
| **Request CT directly** (Fire & forget) | One-off operations without cancellation | Simplest for tasks that don't need special handling |

The discovery system uses **independent CancellationTokenSource** with **cache-based cancellation** for maximum flexibility and reliability.
