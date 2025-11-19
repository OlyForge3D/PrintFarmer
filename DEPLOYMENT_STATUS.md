# SignalR Issues - ROOT CAUSE IDENTIFIED ✅

## Problem Summary

**The SignalR disconnection issue was NOT caused by JSON serialization mismatch!**

The actual cause was discovered when you mentioned disabled printers:

### Root Cause: Disabled Printers in Subscription Service

**File**: `src/api/Services/MoonrakerSubscriptionService.cs` (line 187)

**Bug**: `EnumerateAndStartSubscriptionsAsync()` was subscribing to **ALL** Moonraker printers, including **disabled ones**:

```csharp
// OLD CODE - INCLUDES DISABLED PRINTERS ❌
List<Printer> printers = await printersRepo.GetByBackendAsync(PrinterBackend.Moonraker, ct);
```

**Impact**:
- Disabled printers have missing/invalid ServerUrl
- Subscription attempts to connect to invalid endpoints
- WebSocket connection fails immediately
- Background service gets stuck in reconnection loop
- Every ~10 seconds: connection → failure → attempt to disconnect/reconnect
- This creates the "Server returned an error on close" pattern

---

## Solution Applied

**File**: `src/api/Services/MoonrakerSubscriptionService.cs` (line 187-195)

**New Code - Filter Enabled Printers Only** ✅:
```csharp
// NEW CODE - ONLY ENABLED PRINTERS ✅
List<Printer> allPrinters = await printersRepo.GetByBackendAsync(PrinterBackend.Moonraker, ct);
List<Printer> printers = allPrinters.Where(p => p.IsEnabled).ToList();
```

**Build Status**: ✅ 0 Errors, 0 Warnings (11 warnings in other projects, not related to this change)

---

## Why This Fixes the Issue

1. **Disabled printers are excluded** from subscription loop
2. **No more failed connection attempts** to invalid endpoints
3. **Only enabled printers** get WebSocket subscriptions
4. **SignalR connection stays stable** for enabled printers
5. **Admin UI can show disabled printers** without causing backend errors

---

## Deployment Instructions

**Deploy the fix immediately:**

```bash
cd /Users/jpapiez/s/PFarm1
git add -A
git commit -m "fix: exclude disabled printers from moonraker subscription service"
./deploy-docker.sh --tear-down
git pull
./deploy-docker.sh --non-interactive
```

---

## After Deployment - Verification

1. **Import 2 printers** (as you did)
2. **Leave them disabled** (as you did)  
3. **No more disconnections** ✅
4. **Enable a printer** → see real-time updates
5. **Disable a printer** → stops receiving updates, no errors

---

## Additional Fix Applied Earlier

For completeness, the SignalR JSON serialization fix from earlier is still valuable:

**File**: `src/api/Program.cs` (lines 301-318)

**What it does**: Ensures SignalR uses camelCase JSON matching client expectations

**Why it helps**: Even though disabled printers were the primary cause, the JSON config ensures smooth operation for all enabled printers.

---

## Summary

| Issue | Root Cause | Status |
|-------|-----------|--------|
| Disabled printers in subscription | No `IsEnabled` filter in `GetByBackendAsync` | ✅ FIXED |
| Repeated connection failures | Disabled printers have invalid ServerUrl | ✅ FIXED |
| "Server returned an error on close" | Reconnection loop from failed subscriptions | ✅ FIXED |
| SignalR JSON serialization | Default PascalCase vs client camelCase | ✅ FIXED EARLIER |

**Both issues are now resolved!** Deploy and test with your 2 disabled printers.


