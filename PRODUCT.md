# Product

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

## Users

- **Primary: print-farm operators.** People running a 3D printing farm as a business or production line. They watch fleet state, clear beds, dispatch queued jobs, and react to failures, often while physically moving between printers.
- **Secondary: serious hobbyists and makers.** They run a handful of printers at home and want the same fleet control without a cloud dependency.
- Makerspace, school, and lab staff are an implied audience through roles and permissions, but they are not confirmed as a primary design target.

## Product Purpose

PrintFarmer is a self-hosted system for monitoring and controlling many 3D printers from one place. It covers live status, job queueing and auto-dispatch, integrated slicing (OrcaSlicer/PrusaSlicer), cameras, failure detection, location hierarchies, and cost tracking. Success means an operator can trust what the screen says about every printer and act on it quickly. It also means the farm keeps producing with fewer manual touches.

## Positioning

PrintFarmer stands on two things together:

1. **Self-hosted, backend-agnostic fleet control.** One dashboard covers mixed fleets, including Moonraker/Klipper and PrusaLink printers discovered on the network. Auto-dispatch, slicing, and profile management live in the same system, and nothing requires a vendor cloud.
2. **Honest, safety-first state reporting.** The product never claims what it cannot verify:
   - A command that was accepted is not reported as physically completed.
   - Lost communication is shown as **Unknown**.
   - Bed-clear and ready gates are explicit.
   - Clients never silently replay motion.

## Operating Context

- Operators glance at the web dashboard on a shop-floor monitor, laptop, or tablet. They use the native iOS app away from the desk.
- Typical tasks:
  - Scan fleet status and spot attention items.
  - Confirm the bed is clear so queued work can dispatch.
  - Start, pause, and cancel jobs.
  - Jog and home printers.
  - Watch cameras.
  - Review failure-detection alerts.
  - Slice models against worker profiles.
- Printers sit in a hierarchy of locations, for example Warehouse > Floor > Room > Rack.
- Deployment is self-hosted: Docker for production, native processes for development. Administrators manage users, roles, and system settings in an Admin Control Center.

## Capabilities and Constraints

- **Clients:**
  - React web app (`src/Web/ReactApp/`).
  - Native SwiftUI iOS app (`mobile/`, iOS 17+).
  - Both consume the same `/api/*` contract (camelCase JSON, string enums).
  - The web and iOS apps must stay functionally compatible.
- **Real-time updates:** SignalR, with lowercase event names.
- **Printer backends:** plugins with discovery probes, covering Moonraker and PrusaLink plus others.
- **Motion controls:** Guided (the default) and Expert presentation. The mode is a per-user preference stored on the backend. Neither mode changes permissions or safety protections, and emergency stop is always reachable.
- **Settings:** a URL-driven shell with three scopes (`/settings`, `/admin/settings`, `/admin/manage`). Settings save per group; there is no "Save All".
- **Slicing data sources:** worker profiles (used to select a profile for a new slice job) and user-owned database profiles (on the Slicer Profiles management pages) are distinct and must not be merged.
- **Terminology:** *printer*, *fleet*, *job*, *queue*, *auto-dispatch*, *bed clear / Pending Ready*, *location*, *worker*, *profile*.

## Brand Commitments

- **Name:** PrintFarmer, from the OlyForge3D organization.
- **Assets:**
  - `src/Web/ReactApp/public/printfarmer-logo.svg`
  - `src/Web/ReactApp/public/icons/`
  - `mobile/PrintFarmer/Assets.xcassets/AppLogo.imageset/`
- **Voice:** factual and precise about state and uncertainty. It never overstates success. *(Inferred from README and product behavior.)*

## Evidence on Hand

- Product documentation lives in `docs/`, including `FEATURES.md`, `ARCHITECTURE.md`, `GETTING_STARTED.md`, and `API.md`.
- Printer model imagery comes from OrcaSlicer cover assets under `src/Web/ReactApp/public/assets/`, with generic motion-type SVG fallbacks.
- No customer testimonials, adoption numbers, benchmarks, or pricing exist in the repo. Future work must not fabricate them.

## Product Principles

1. **Truth over reassurance.** State, outcomes, and uncertainty are shown as they are. "Unknown" is a valid answer, and a false "Done" is a defect.
2. **Safety is never a mode.** Presentation choices like Guided/Expert or compact/detailed never weaken permissions, protections, or emergency-stop access.
3. **Fleet at a glance, printer on demand.** Make the whole farm scannable first, and keep full per-printer depth one step away.
4. **Mixed fleets are normal.** Never assume a single vendor or backend. Show capability honestly per printer.
5. **Self-hosted by default.** Features must work without a vendor cloud.

## Accessibility & Inclusion

- Web UI targets **WCAG 2.2 Level AA** and should exceed it where practical. This is required by `.github/instructions/a11y.instructions.md`.
- Controls must be keyboard- and touch-accessible. Help offered in Expert mode must stay reachable by keyboard and touch.
- The iOS app follows platform accessibility expectations (Dynamic Type, VoiceOver).
