# Slicer Hub Contract

This document describes the SignalR messages emitted by the API for slicer registry and progress events.

Events emitted by the slicer hub (`/hubs/slicers`):

- `SlicerRegistered` — payload: { id: Guid, name: string, version: string?, host: string?, maxConcurrentJobs: int, status: string }
- `SlicerHeartbeat` — payload: { id: Guid, status: string, freeSlots: int? }
- `SlicerDeregistered` — payload: { id: Guid }

Client usage:
- Connect to `/hubs/slicers` using SignalR client.
- Subscribe to events via `connection.on("SlicerRegistered", handler)` etc.
- Optionally join a monitoring group on the Progress hub for aggregated updates.
