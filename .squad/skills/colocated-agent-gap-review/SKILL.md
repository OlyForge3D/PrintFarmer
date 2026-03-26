---
name: "colocated-agent-gap-review"
description: "Compare a co-located upstream printer agent with PrintFarmer’s centralized backend and separate true backend gaps from agent-only behavior."
domain: "backend-integrations"
confidence: "high"
source: "earned"
---

## Context

Use this when PrintFarmer is being compared to an upstream plugin or agent that runs next to a printer service such as Moonraker, OctoPrint, or Klipper. These comparisons are easy to misread because the upstream agent can depend on localhost access, printer-side credentials, or relay infrastructure that a centralized backend does not have.

## Patterns

- Start by classifying the upstream component: **ML client only** vs **full co-located agent**. If it links to a cloud account, opens printer-local WebSockets, captures webcam frames locally, or tunnels HTTP/WebSocket traffic, it is a co-located agent.
- Separate **product gaps** from **architecture privileges**:
  - Product gaps are things PrintFarmer still needs in its own architecture, such as snapshot delivery, proxying, auth propagation, or printer-aware validation.
  - Architecture privileges are things the upstream agent gets “for free” because it runs on the printer host, such as localhost webcam access, Moonraker API-key bootstrap, Janus/WebRTC relay, or cloud passthru tunnels.
- For Obico-style ML integrations, focus the actionable review on:
  - how snapshots reach the ML service,
  - whether private/authenticated webcam URLs are reachable,
  - whether runtime fallback and admin validation use the same contract,
  - whether Moonraker-specific auth or discovery assumptions hold in PrintFarmer.
- Prefer a **delivery strategy abstraction** over one hard-coded path:
  - direct camera URL when the ML server can fetch it,
  - PrintFarmer proxy URL when the camera is private/authenticated,
  - local byte upload when URL-based fetch is impossible or unsupported.
- Treat tunnel, passthru, and Janus relay features as non-goals unless the product explicitly needs remote Obico app compatibility.

## Examples

- Upstream co-located agent: `https://github.com/TheSpaghettiDetective/moonraker-obico`
- PrintFarmer failure detection path:
  - `src/infra/Services/FailureDetection/PrintFailureMonitorService.cs`
  - `src/infra/Services/FailureDetection/ObicoFailureDetectionService.cs`
  - `src/api/Controllers/ObicoServerController.cs`
  - `src/api/Controllers/PrintersController.cs`
  - `src/backends/Farm.Backend.Plugin.Moonraker/MoonrakerClient.cs`

## Anti-Patterns

- Treating full upstream agent parity as required when PrintFarmer only needs ML inference parity.
- Filing “missing relay/tunnel/WebRTC” as a bug when the centralized backend has no printer-side execution context.
- Validating only the ML endpoint and ignoring whether the chosen snapshot path is actually reachable from that ML server.
- Assuming camera URLs are enough even when auth headers, localhost-only hosts, or stream-only cameras are involved.
