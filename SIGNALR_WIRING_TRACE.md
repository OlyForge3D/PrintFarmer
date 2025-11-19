# SignalR Wiring: Complete Data Flow Trace

## Architectural Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            PRINTFARMER SIGNALR FLOW                         │
└─────────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────┐                                ┌──────────────────┐
│  REACT CLIENT            │                                │  ASP.NET API     │
│  (http://localhost:3000) │                                │  (Port 5245)     │
└──────────────────────────┘                                └──────────────────┘
         │                                                            │
         │ 1. BUILD CONNECTION                                       │
         │────────────────────────────────────────────────────────→  │
         │    new HubConnectionBuilder()                             │
         │    .withUrl("/hubs/printers")                             │
         │    .build()                                               │
         │    .start()                                               │
         │                                                            │
         │ 2. HANDSHAKE (HTTP/1.1)                                  │
         │────────────────────────────────────────────────────────→  │
         │    GET /hubs/printers                                     │
         │    Origin: http://localhost:3000                          │
         │    [CORS preflight + headers]                             │
         │                                                      Program.cs:176
         │                                                      (CORS policy)
         │                                                            │
         │ 3. UPGRADE TO WEBSOCKET                                  │
         │←────────────────────────────────────────────────────────  │
         │    101 Switching Protocols                                │
         │    [WebSocket handshake complete]                         │
         │                                                      Program.cs:635
         │                                                      MapHub<PrinterHub>
         │                                                            │
         │ 4. REGISTER EVENT HANDLERS (CLIENT-SIDE)                 │
         │    ┌─ connection.on("printerupdated", cb)                │
         │    │  connection.on("discoveryprogress", cb)             │
         │    │  connection.on("jobqueueupdate", cb)                │
         │    └─ printer-signalr.ts:143+                            │
         │                                                            │
         │                                                            │
         │                    ┌──────────────────────────────────┐  │
         │                    │ BACKEND: MOONRAKER POLLING       │  │
         │                    │ (Every ~10 seconds)              │  │
         │                    └──────────────────────────────────┘  │
         │                            │                              │
         │                            │ Poll Moonraker printer       │
         │                            │ status via WebSocket         │
         │                            │                              │
         │                            └─→ Build PrinterStatusUpdate  │
         │                                 ├─ Id (Guid)              │
         │                                 ├─ IsOnline (bool)        │
         │                                 ├─ State (string)         │
         │                                 └─ ... 10+ properties     │
         │                                                      Models.cs:218
         │                                                      PrinterStatusUpdate
         │                                                            │
         │                    ┌──────────────────────────────────┐  │
         │                    │ SERIALIZE WITH JSON              │  │
         │                    │ ❌ USES DEFAULT OPTIONS          │  │
         │                    │ (NOT camelCase!)                 │  │
         │                    └──────────────────────────────────┘  │
         │                                                            │
         │                    DEFAULT JSON (PascalCase):             │
         │                    {                                       │
         │                      "Id": "guid-uuid",                    │
         │                      "IsOnline": true,                     │
         │                      "State": "Idle",                      │
         │                      "Progress": 0.5,                      │
         │                      "JobName": "print.gcode"              │
         │                      "HotendTemp": 205.0,                  │
         │                      "HotendTarget": 210.0                 │
         │                    }                                       │
         │                                                      Program.cs:300
         │                                                      AddSignalR() ← NO CONFIG
         │                                                            │
         │ 5. BROADCAST EVENT (TO ALL CLIENTS)                      │
         │←────────────────────────────────────────────────────────  │
         │    WebSocket message:                                     │
         │    {                                                       │
         │      "type": 1,                            (InvocationMessage)
         │      "target": "printerupdated",                          │
         │      "arguments": [                                        │
         │        {                                                   │
         │          "Id": "guid-uuid",       ← WRONG: should be "id"  │
         │          "IsOnline": true,        ← WRONG: should be "isOnline"
         │          "State": "Idle",         ← WRONG: should be "state"
         │          ...                                               │
         │        }                                                   │
         │      ]                                                     │
         │    }                                                       │
         │                                                            │
         │ 6. RECEIVE EVENT (CLIENT-SIDE)                           │
         │    ↓                                                       │
         │    Parse JSON: {"Id": "uuid", "IsOnline": true, ...}     │
         │    ↓                                                       │
         │    Call handler: handlePrinterUpdated(parsedObject)      │
         │    ↓                                                       │
         │    printer-signalr.ts:83                                  │
         │    ┌──────────────────────────────────────────────────┐  │
         │    │ const handlePrinterUpdated = (status) => {      │  │
         │    │   try {                                          │  │
         │    │     if (debug) console.debug("printerupdated", {│  │
         │    │       id: status.id,        ← UNDEFINED!        │  │
         │    │       state: status.state,  ← UNDEFINED!        │  │
         │    │       isOnline: status.isOnline ← UNDEFINED!    │  │
         │    │     });                                          │  │
         │    │     this.lastStatuses.set(status.id, status)    │  │
         │    │     ↓                                            │  │
         │    │     status.id is undefined                      │  │
         │    │     ↓                                            │  │
         │    │     Exception thrown!                           │  │
         │    │   } catch (e) {                                 │  │
         │    │     console.error("Printer status cb error:", e)│  │
         │    │   }                                             │  │
         │    │ }                                               │  │
         │    └──────────────────────────────────────────────────┘  │
         │                                                            │
         │    Exception in callback handler                          │
         │    ↓                                                       │
         │    SignalR detects error                                  │
         │    ↓                                                       │
         │    Closes WebSocket                                       │
         │                                                            │
         │ 7. DISCONNECTION (CLIENT-SIDE)                           │
         │    ↓                                                       │
         │    connection.onclose({ code: 1011, reason: "error" })  │
         │    ↓                                                       │
         │    Console: "Close message from server"                  │
         │    ↓                                                       │
         │    Trigger reconnect (5s delay)                          │
         │                                                            │
         │    Repeat from step 1 → INFINITE LOOP 😞                │
         │                                                            │

┌──────────────────────────────────────────────────────────────────────────────┐
│                            THE PROBLEM IN DETAIL                             │
└──────────────────────────────────────────────────────────────────────────────┘

CONFIGURATION MISMATCH:

Controllers (Program.cs:109-118):
  ✅ Configured with camelCase naming: PropertyNamingPolicy = JsonNamingPolicy.CamelCase
  
SignalR (Program.cs:300):
  ❌ NOT configured - uses DEFAULT options
  ❌ Default = PascalCase property names
  
Client (printer-signalr.ts:80+):
  ✅ Expects camelCase: status.id, status.isOnline, status.state
  
Result:
  ❌ Server sends PascalCase, client expects camelCase
  ❌ Properties undefined/null
  ❌ Exception thrown
  ❌ WebSocket closes
  ❌ Client reconnects after 5s
  ❌ Repeat forever


┌──────────────────────────────────────────────────────────────────────────────┐
│                              THE FIX (High Level)                            │
└──────────────────────────────────────────────────────────────────────────────┘

Change from:
  builder.Services.AddSignalR();

To:
  builder.Services.AddSignalR()
  .AddJsonProtocol(options =>
  {
      options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
      options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
      options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
  });

This ensures SignalR serializes with SAME options as Controllers ✅


┌──────────────────────────────────────────────────────────────────────────────┐
│                        CALL STACK: WHERE IT BREAKS                           │
└──────────────────────────────────────────────────────────────────────────────┘

Backend:
  MoonrakerSubscriptionService.ProcessStatusUpdateAsync() [line 900+]
    ↓
    new PrinterStatusUpdate(...)  ← Creates model with PascalCase properties
    ↓
    hub.Clients.All.SendAsync("printerupdated", update, ct)  ← Broadcasts
      ↓
      [SignalR Serialization]
      JSON.Serialize(update, defaultOptions)  ← DEFAULT = PascalCase
      ↓
      Sends WebSocket frame: {"Id": "...", "IsOnline": true, ...}

Frontend:
  connection.on("printerupdated", handlePrinterUpdated)  ← Receives frame
    ↓
    JSON.parse(frame.arguments[0])  ← Client-side parse
    ↓
    {Id: "...", IsOnline: true, ...}  ← PascalCase object
    ↓
    handlePrinterUpdated(parsedObject)
      ↓
      this.lastStatuses.set(status.id, status)  ← status.id UNDEFINED!
      ↓
      TypeError: Cannot read property from undefined
      ↓
      catch (e) → console.error("Printer status cb error:")
      ↓
      SignalR error handler fires
      ↓
      connectionClosed() → "Server returned an error on close"
      ↓
      ❌ DISCONNECT


┌──────────────────────────────────────────────────────────────────────────────┐
│                    OTHER WIRING ISSUES (Lower Priority)                      │
└──────────────────────────────────────────────────────────────────────────────┘

Issue: Client calls hub method that doesn't exist

  printer-signalr.ts:298+
    await this.connection.invoke("RequestPrinterStatus", printerId)
    
  BUT PrinterHub (line 1+):
    No RequestPrinterStatus method defined ❌
    
  Result:
    Server responds with error
    Client throws
    Connection may close
    
  Fix: Add method to PrinterHub:
    public async Task RequestPrinterStatus(string printerId) { ... }


┌──────────────────────────────────────────────────────────────────────────────┐
│                           WIRING CHECKLIST                                   │
└──────────────────────────────────────────────────────────────────────────────┘

COMPONENT CHECKLIST:

[Backend] Program.cs - CORS Policy
  Location: Line 176-183
  Status: ✅ Configured
  Check: ALLOWED_ORIGINS env var includes client origin

[Backend] Program.cs - Controllers JSON Config
  Location: Line 109-118
  Status: ✅ camelCase configured
  Check: PropertyNamingPolicy = JsonNamingPolicy.CamelCase

[Backend] Program.cs - SignalR Registration
  Location: Line 300
  Status: ❌ NOT CONFIGURED - CRITICAL ISSUE
  Fix: AddJsonProtocol with camelCase config

[Backend] PrinterHub - Hub Methods
  Location: Hubs/PrinterHub.cs
  Status: ⚠️ Missing RequestPrinterStatus method
  Fix: Add public async Task RequestPrinterStatus(string printerId)

[Backend] MoonrakerSubscriptionService - Error Handling
  Location: Line 912+
  Status: ✅ Recently improved with try-catch
  Check: Logs show "Failed to send status update: ..." for errors

[Frontend] printer-signalr.ts - Event Listeners
  Location: Line 143+
  Status: ✅ Correctly registered
  Check: All event names lowercase: "printerupdated", etc.

[Frontend] printer-signalr.ts - Handler Callbacks
  Location: Line 80+
  Status: ✅ Expecting camelCase properties
  Check: status.id, status.isOnline, status.state

[Frontend] Types - Model Definitions
  Location: src/types/api.ts (PrinterStatusUpdate)
  Status: ✅ camelCase properties defined
  Check: Matches server JSON after serialization

[Network] CORS Headers
  Location: Program.cs middleware
  Status: ✅ Configured in CORS policy
  Check: Pre-flight requests succeed (OPTIONS /hubs/printers)


┌──────────────────────────────────────────────────────────────────────────────┐
│                      FILES INVOLVED IN THIS FLOW                             │
└──────────────────────────────────────────────────────────────────────────────┘

Backend:
  /src/api/Program.cs                           - Registration & middleware
  /src/api/Hubs/PrinterHub.cs                  - Hub definition
  /src/api/Services/MoonrakerSubscriptionService.cs  - Event broadcast
  /src/shared/Models.cs                        - PrinterStatusUpdate model

Frontend:
  /src/Web/ReactApp/src/services/printer-signalr.ts  - Connection & handlers
  /src/Web/ReactApp/src/services/printerHubService.ts - Deprecated duplicate?
  /src/Web/ReactApp/src/types/api.ts           - TypeScript interfaces
  /src/Web/ReactApp/src/utils/apiUrlHelpers.ts - Hub URL resolution

Configuration:
  /src/infra/Settings/SignalRSettings.cs       - Settings POCO
  /src/shared/SignalRSettingsDto.cs            - DTO for API
```

## Complete Event Name Reference

| Event | Backend Sends | Frontend Listens | Usage |
|-------|---------------|------------------|-------|
| `printerupdated` | MoonrakerSubscriptionService | printer-signalr.ts:143 | Printer status updates |
| `discoveryprogress` | NetworkDiscoveryService | printer-signalr.ts:151 | Discovery progress |
| `discoveryprinterfound` | NetworkDiscoveryService | printer-signalr.ts:152 | Printer found during discovery |
| `discoverycompleted` | NetworkDiscoveryService | printer-signalr.ts:153 | Discovery finished |
| `jobqueueupdate` | SliceJobEventService | printer-signalr.ts:145 | Job queue changes |
| `slicingprogress` | SlicerProgressHub | slicer-signalr.ts | Slicing progress updates |
| `slicingcompleted` | SlicerProgressHub | slicer-signalr.ts | Slicing job completed |
| `slicingfailed` | SlicerProgressHub | slicer-signalr.ts | Slicing job failed |

**Note**: All event names are **lowercase** by convention. ✅

