# Backend Selection UI - Visual Mockup

## PrinterDiscoveryModal - Before Start

```
┌─────────────────────────────────────────────────────────────────┐
│                        Discover Printers                    [X] │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Scan your network for compatible 3D printers                  │
│                                                                 │
│  Select backends to scan:                                       │
│  ┌───────────────────────────────────────────────────────────┐ │
│  │  ☑ Moonraker    ☑ PrusaLink    ☐ SDCP    ☐ OctoPrint    │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌──────────────────────────┐                                  │
│  │  🔍 Start Network Scan   │                                  │
│  └──────────────────────────┘                                  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## During Scan

```
┌─────────────────────────────────────────────────────────────────┐
│                        Discover Printers                    [X] │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Scan your network for compatible 3D printers                  │
│                                                                 │
│  Select backends to scan:                                       │
│  ┌───────────────────────────────────────────────────────────┐ │
│  │  ☑ Moonraker    ☑ PrusaLink    ☐ SDCP    ☐ OctoPrint    │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌──────────────────────────┐                                  │
│  │  🔄 Scanning...          │ (disabled)                       │
│  └──────────────────────────┘                                  │
│                                                                 │
│  ╔══════════════════════════════════════════════════════════╗  │
│  ║ ▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓▓░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  65% ║  │
│  ╚══════════════════════════════════════════════════════════╝  │
│  Session: abc-123-def                                           │
│  Networks: 192.168.1.0/24 (auto-detected)                      │
│  Scanning 192.168.1.0/24 - 192.168.1.105                       │
│  165 of 254 IPs scanned • 3 printers found                     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Validation - No Backends Selected

```
┌─────────────────────────────────────────────────────────────────┐
│                        Discover Printers                    [X] │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Scan your network for compatible 3D printers                  │
│                                                                 │
│  Select backends to scan:                                       │
│  ┌───────────────────────────────────────────────────────────┐ │
│  │  ☐ Moonraker    ☐ PrusaLink    ☐ SDCP    ☐ OctoPrint    │ │
│  └───────────────────────────────────────────────────────────┘ │
│                                                                 │
│  ┌──────────────────────────┐                                  │
│  │  🔍 Start Network Scan   │ (disabled)                       │
│  └──────────────────────────┘                                  │
│  ⚠️ Please select at least one backend to scan                 │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## Key Features

1. **Default Selection**: Moonraker and PrusaLink are selected by default
2. **Validation**: Button is disabled if no backends selected
3. **Error Message**: Shows warning when no backends selected
4. **Disabled During Scan**: Checkboxes are disabled while scanning
5. **Clear Feedback**: Users know exactly what will be scanned

## Benefits

- **Faster Scans**: Scan only the backends you need
- **Reduced Network Load**: Less traffic when scanning specific backends
- **Better UX**: Clear selection and immediate feedback
- **Flexible**: Can scan any combination of backends
