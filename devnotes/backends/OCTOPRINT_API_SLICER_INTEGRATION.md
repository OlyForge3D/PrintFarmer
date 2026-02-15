# OctoPrint API for Slicer Integration

## Overview

PrintFarmer implements a **minimal OctoPrint-compatible API** that allows popular slicers (PrusaSlicer, OrcaSlicer, SuperSlicer) to upload G-code files directly to PrintFarmer. This eliminates manual file transfer and integrates seamlessly into existing slicer workflows.

> **Note**: This is different from OctoPrint *backend* support. This feature makes PrintFarmer act as an OctoPrint *server* for slicers, not connect to OctoPrint printers.

## Architecture

```mermaid
graph TB
    subgraph "Slicer Workstation"
        PS[PrusaSlicer/OrcaSlicer]
    end
    
    subgraph "PrintFarmer Server"
        API[OctoPrint API<br/>api/octoprint/*]
        Auth[API Key<br/>Authentication]
        FS[G-code File<br/>Storage]
        Queue[Print Job<br/>Queue]
        Approval[Print Approval<br/>Service]
    end
    
    subgraph "3D Printers"
        P1[Printer 1]
        P2[Printer 2]
        P3[Printer 3]
    end
    
    PS -->|1. Upload G-code<br/>POST /files/local| API
    API --> Auth
    Auth -->|Valid API Key| FS
    FS -->|2. Store file| Queue
    Queue -->|3. Create job| Approval
    Approval -->|4. Pending approval| Queue
    Queue -.->|5. After approval| P1
    Queue -.->|5. After approval| P2
    Queue -.->|5. After approval| P3
    
    style API fill:#4CAF50
    style Auth fill:#FFC107
    style Approval fill:#FF9800
```

## API Endpoints

PrintFarmer implements **only the essential endpoints** required by slicers:

| Endpoint | Method | Auth | Purpose |
|----------|--------|------|---------|
| `/api/octoprint/version` | GET | Optional | Version check (slicers verify compatibility) |
| `/api/octoprint/server` | GET | Optional | Server status |
| `/api/octoprint/files/local` | POST | **Required** | Upload G-code file |

### Why minimal?

Slicers only need these three endpoints to function. File management (listing, deleting) is better handled through PrintFarmer's web UI.

## Authentication Flow

```mermaid
sequenceDiagram
    participant User
    participant PF_UI as PrintFarmer UI
    participant PF_API as PrintFarmer API
    participant Slicer
    
    User->>PF_UI: Navigate to API Keys page
    User->>PF_UI: Create API Key<br/>(name: "PrusaSlicer")
    PF_UI->>PF_API: POST /users/{userId}/apikeys
    PF_API->>PF_UI: Return key (one-time display)
    PF_UI->>User: Show key + copy button
    
    Note over User,PF_UI: Key never shown again!
    
    User->>Slicer: Configure OctoPrint settings<br/>URL + API Key
    Slicer->>PF_API: POST /api/octoprint/files/local<br/>X-Api-Key: {key}
    PF_API->>PF_API: Validate API Key
    alt Valid Key
        PF_API->>Slicer: 202 Accepted
    else Invalid Key
        PF_API->>Slicer: 401 Unauthorized
    end
```

## Upload & Print Workflow

```mermaid
graph LR
    subgraph "Slicer Upload"
        S[Slicer] -->|POST with<br/>print=true| A[OctoPrint API]
    end
    
    subgraph "PrintFarmer Processing"
        A -->|1. Validate| B{API Key<br/>Valid?}
        B -->|No| E[401 Error]
        B -->|Yes| C[Store G-code]
        C -->|2. Create| D[Print Job]
        D -->|3. Create| F[Print Approval]
    end
    
    subgraph "User Approval"
        F -->|Status:<br/>Pending| G[Admin reviews<br/>in Web UI]
        G -->|Approve| H[Job moves to<br/>Print Queue]
        G -->|Reject| I[Job deleted]
    end
    
    subgraph "Print Execution"
        H -->|Scheduled| J[Printer assigned]
        J -->|Starts printing| K[Complete]
    end
    
    style B fill:#FFC107
    style F fill:#FF9800
    style G fill:#2196F3
    style H fill:#4CAF50
```

## API Key Management

### Key Features

```mermaid
graph TD
    A[API Keys Page] --> B[Create New Key]
    A --> C[View All Keys]
    A --> D[Toggle Active/Inactive]
    A --> E[Rotate Key]
    A --> F[Delete Key]
    
    B -->|One-time display| G[Copy to Clipboard]
    C -->|Shows| H[Name, Status, Created Date]
    D -->|Security| I[Disable without deletion]
    E -->|Generate new value| J[Old key invalidated]
    F -->|Confirmation required| K[Permanent deletion]
    
    style B fill:#4CAF50
    style G fill:#FF5722
    style D fill:#FFC107
    style E fill:#FF9800
    style F fill:#F44336
```

### Security Model

| Feature | Implementation |
|---------|---------------|
| **Storage** | SHA-256 hashed (never plaintext) |
| **Display** | Shown only once on creation |
| **Transmission** | HTTPS recommended (encrypt in transit) |
| **Scope** | Per-user (not global) |
| **Audit** | All uploads logged with API key ID |
| **Revocation** | Toggle inactive or delete |

## Rate Limiting

```mermaid
graph LR
    A[Upload Request] -->|Check| B{Rate Limit<br/>Exceeded?}
    B -->|No| C[Process Upload]
    B -->|Yes| D[429 Too Many<br/>Requests]
    
    C -->|Increment| E[Counter:<br/>60/min per key]
    E -->|After 1 min| F[Reset Counter]
    
    style B fill:#FFC107
    style D fill:#F44336
    style C fill:#4CAF50
```

**Default Limits:**
- 60 uploads per minute per API key
- 50 MB max file size (configurable)

## Slicer Configuration

### Quick Setup Matrix

| Slicer | Version | Upload Support | Auto-Print | Notes |
|--------|---------|---------------|------------|-------|
| **PrusaSlicer** | 2.0+ | ✅ | ✅ | OctoPrint integration |
| **OrcaSlicer** | All | ✅ | ✅ | OctoPrint integration |
| **SuperSlicer** | All | ✅ | ✅ | Fork of PrusaSlicer |
| **Cura** | 4.0+ | ⚠️ | ⚠️ | Plugin required |

### Configuration Steps (Visual)

```mermaid
graph TD
    A[Start] --> B[Open Slicer Preferences]
    B --> C[Find OctoPrint/Network Settings]
    C --> D[Add New Printer Connection]
    D --> E[Enter Details:<br/>URL + API Key]
    E --> F[Test Connection]
    F -->|Success| G[✅ Ready to Upload]
    F -->|Failed| H[Check URL/Key]
    H --> E
    
    style G fill:#4CAF50
    style H fill:#F44336
```

**Example Configuration:**
```
Name:     PrintFarmer
Host:     http://printfarmer.local:5245
Port:     (leave empty - included in host)
API Key:  <paste your key here>
HTTPS:    Unchecked (unless configured)
```

## Approval Workflow

### Why Approval Required?

```mermaid
mindmap
  root((Print Approval))
    Quality Control
      Review before print
      Catch bad slicing
      Verify material
    Security
      Prevent unauthorized prints
      Review file source
      User accountability
    Resource Management
      Prioritize jobs
      Assign optimal printer
      Schedule efficiently
```

### Approval UI Flow

```mermaid
stateDiagram-v2
    [*] --> Uploaded: Slicer uploads file
    Uploaded --> Pending: Job created with print=true
    Pending --> Reviewing: Admin opens approvals page
    Reviewing --> Approved: Admin clicks "Approve"
    Reviewing --> Rejected: Admin clicks "Reject"
    Approved --> Queued: Job enters print queue
    Queued --> Printing: Printer assigned
    Printing --> [*]: Print complete
    Rejected --> [*]: Job deleted
```

## Troubleshooting

### Common Issues & Solutions

```mermaid
graph TD
    A[Upload Fails] --> B{Error Code?}
    B -->|401| C[Invalid API Key]
    B -->|429| D[Rate Limit Hit]
    B -->|500| E[Server Error]
    
    C --> F[Check key is active]
    C --> G[Regenerate key]
    
    D --> H[Wait 1 minute]
    D --> I[Reduce upload frequency]
    
    E --> J[Check server logs]
    E --> K[Verify network access]
    
    style C fill:#F44336
    style D fill:#FF9800
    style E fill:#F44336
```

### Diagnostic Commands

```bash
# Test version endpoint (should return JSON)
curl http://printfarmer.local:5245/api/octoprint/version

# Test upload with API key
curl -X POST \
  -H "X-Api-Key: YOUR_KEY_HERE" \
  -F "file=@test.gcode" \
  http://printfarmer.local:5245/api/octoprint/files/local

# Check server logs
docker logs printfarmer-api | tail -50
```

## Comparison: OctoPrint API vs PrintFarmer Features

```mermaid
graph LR
    subgraph "OctoPrint (Full API)"
        O1[Upload]
        O2[List Files]
        O3[Delete Files]
        O4[Job Control]
        O5[Settings]
        O6[System]
    end
    
    subgraph "PrintFarmer (Minimal for Slicers)"
        P1[Upload ✅]
        P2[Version Check ✅]
        P3[Server Status ✅]
    end
    
    subgraph "PrintFarmer Web UI"
        W1[File Browser]
        W2[Job Management]
        W3[Approval Queue]
        W4[Printer Control]
    end
    
    O1 -.-> P1
    O2 -.->|Use Web UI| W1
    O3 -.->|Use Web UI| W1
    O4 -.->|Use Web UI| W2
    
    style P1 fill:#4CAF50
    style P2 fill:#4CAF50
    style P3 fill:#4CAF50
```

## Benefits

| Benefit | Description |
|---------|-------------|
| 🚀 **Faster Workflow** | Upload directly from slicer - no manual file transfer |
| 🔒 **Secure** | API keys with rate limiting and audit logging |
| ✅ **Quality Control** | Approval step prevents bad prints |
| 📊 **Centralized** | All files in one place with metadata |
| 🎯 **Multi-Printer** | Upload once, assign to any printer |
| 📱 **Mobile Friendly** | Approve prints from phone via web UI |

## Future Enhancements

Potential additions (not yet implemented):

```mermaid
graph TD
    A[Current] --> B[Upload API]
    
    C[Future: Job Status] --> D[Query job progress<br/>from slicer]
    E[Future: Printer Selection] --> F[Specify printer<br/>in upload request]
    G[Future: Auto-Approval] --> H[Rules-based approval<br/>for trusted users]
    
    style A fill:#4CAF50
    style C fill:#9E9E9E
    style E fill:#9E9E9E
    style G fill:#9E9E9E
```

## Related Documentation

- 📖 **[Slicer Configuration Guide](SLICER_CONFIGURATION.md)** - Step-by-step setup for PrusaSlicer/OrcaSlicer
- 🔐 **[API Authentication](API.md#authentication)** - Complete API documentation
- 🏗️ **[Architecture Overview](ARCHITECTURE.md)** - PrintFarmer system architecture
- 🔧 **[OctoPrint Backend Integration](../dev/OctoPrint_Integration_Plan.md)** - Different feature (managing OctoPrint printers)

## Quick Reference Card

```
┌─────────────────────────────────────────────────────────┐
│          OCTOPRINT API SLICER INTEGRATION               │
├─────────────────────────────────────────────────────────┤
│ Purpose:  Enable slicers to upload G-code to PrintFarmer│
│ Auth:     API Keys (generate in Web UI)                 │
│ Rate:     60 uploads/min per key                        │
│ Size:     50 MB max file size                           │
├─────────────────────────────────────────────────────────┤
│ ENDPOINTS                                                │
│   GET  /api/octoprint/version       (no auth)          │
│   GET  /api/octoprint/server        (no auth)          │
│   POST /api/octoprint/files/local   (API key required)  │
├─────────────────────────────────────────────────────────┤
│ WORKFLOW                                                 │
│   1. Create API Key in PrintFarmer UI                  │
│   2. Configure slicer with URL + Key                   │
│   3. Slice model and "Send to OctoPrint"               │
│   4. File uploads → Pending Approval                   │
│   5. Approve in Web UI → Print Queue                   │
│   6. Printer starts printing                            │
├─────────────────────────────────────────────────────────┤
│ SLICER SETUP                                            │
│   Host:    http://printfarmer.local:5245                │
│   API Key: <from PrintFarmer → API Keys page>         │
│   HTTPS:   ❌ (unless you configured it)                │
└─────────────────────────────────────────────────────────┘
```

## Status

✅ **Feature Status**: Complete and production-ready

| Component | Status |
|-----------|--------|
| API Endpoints | ✅ Implemented |
| API Key Management | ✅ Implemented |
| Upload Workflow | ✅ Tested |
| Approval Integration | ✅ Working |
| Documentation | ✅ Complete |
| Slicer Compatibility | ✅ Verified (PrusaSlicer/OrcaSlicer) |

Last Updated: 2026-01-15
