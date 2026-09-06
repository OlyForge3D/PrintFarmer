# PrintFarmer

iOS app for managing 3D printer farms.

## About

PrintFarmer is a SwiftUI-based iOS application for monitoring and managing
multiple 3D printers across one or more registered PrintFarmer servers. Features
include printer status monitoring, filament/spool management, job queue viewing,
server switching, and real-time updates via SignalR.

## Tech Stack

- **Language:** Swift
- **UI Framework:** SwiftUI
- **Minimum Target:** iOS 17+
- **Concurrency:** Swift Concurrency (async/await)
- **Architecture:** MVVM with repository pattern
- **Backend:** [PrintFarmer API](https://github.com/OlyForge3D) (ASP.NET Core)

## Requirements

- Xcode 26+
- iOS 17+ deployment target

## Getting Started

1. Clone the repository:
   ```bash
   git clone https://github.com/OlyForge3D/PFarm-Ios.git
   cd PFarm-Ios
   ```

2. Open `PrintFarmer.xcodeproj` in Xcode.

3. Optional for development: set the `PRINTFARMER_API_URL` environment variable
   in your Xcode scheme to seed the initial server. For local PrintFarmer
   development, use `http://localhost:5245`.

4. Build and run on a simulator or device (iOS 17+).

## Server Configuration

The app supports multiple registered PrintFarmer backend servers. Server
registrations are stored locally in UserDefaults on the device, and each server
keeps its own Keychain-stored credentials.

### Managing Servers

- On first launch, register a PrintFarmer server before signing in.
- After setup, open **Settings** → **Manage Servers** to add, edit, or delete servers.
- The server editor normalizes URLs and rejects duplicates.
- Use **Check Connection** to verify reachability. The app checks `/health` and
  `/healthz`; network failures are shown in the editor and saved status appears
  in the server list.

### Switching Servers

- On iPhone, use the toolbar server switcher from the main app screens.
- On iPad, use the server switcher in the sidebar.
- Switching servers rebuilds the app's API, authentication, and SignalR services
  for the newly active server.

### Navigation Shell

The compact (iPhone) layout picks one of two shells depending on the size and
staffing of the connected server. Both shells reach the same destinations —
growth expands the layout, it does not relocate features.

- **Simple** (solo / owner-operator). Four tabs: **Attention · Farm · Inventory
  · Oversight**. There is no mode control. Oversight is a single hub tab that
  groups Dashboard, Dispatch, Filament Coverage, Maintenance, Analytics,
  Predictive Insights, Job History, Job Timeline, Locations, Uptime & Reliability
  and a row into the Navigation setting.
- **Two modes** (staffed farm). A **Floor | Oversight** control is pinned at the
  top of every tab root of both modes.
  - Floor tabs: **Attention · Farm · Tasks · Inventory**.
  - Oversight tabs: **Overview · Fleet · Jobs · Upkeep · Reports**.

The regular-width iPad layout shows the operator destinations as a
`NavigationSplitView` sidebar.

#### How the shell is chosen

The app derives the shell from server-observed farm counts returned by the
authenticated endpoint **`GET /api/system/farm-shape`**
(`{ accountCount, locationCount, printerCount }`, sent with `Cache-Control:
no-store`). This is a separate endpoint from
`GET /api/system/capabilities`, which stays anonymous and unchanged.

An **absent response** — a 401, 404, timeout, or an older server that does not
expose the endpoint — is treated as *shape unknown ⇒ Simple shell*, and the
in-context upgrade offer is suppressed entirely: the app never offers an
upgrade on evidence it does not have.

> **⚠️ `shiftPlanEnabled` is a *negative* signal only.** The flag defaults to
> `true`, so a stock server reads as "on" whether an admin has thought about
> shifts or not. Only an explicit `shiftPlanEnabled == false` is used as
> evidence — a `true` value never demonstrates a staffed farm. Fleet size
> (`printerCount`) is deliberately not a signal either: a solo owner running a
> 40-printer farm would otherwise read as staffed. Reading either signal
> "positively" is the fastest way to reintroduce the bug the redesign closed.

The rule the app applies in `Automatic` mode:

| Condition (evaluated in order) | Result |
|---|---|
| Farm shape unknown (endpoint absent / error) | **Simple** — and no upgrade offer |
| `shiftPlanEnabled == false` | **Simple** — server explicitly says no shifts |
| Signed-in user is not `farm_admin` | **Simple** — no upgrade offer |
| `accountCount >= 2` | **Two modes** |
| `locationCount >= 2` | **Two modes** |
| otherwise | **Simple** |

Role gating only affects the initial shell; it does **not** remove the
Oversight destinations. Content inside every destination remains
permission-gated by the API, unchanged from today.

The Tasks tab's visibility is a separate concern from the shell choice: Tasks
continues to be governed purely by `shiftPlanEnabled`.

#### Overriding the shell — Settings → Navigation

Open **Settings → Navigation** to override the derived layout:

- **Automatic** (default) — matches the layout to this server, and explains in
  plain language which counts and flags drove the choice.
- **Simple** — force the Simple shell.
- **Two modes** — force the Two modes shell.

The preference is stored **per server** (keyed on the server registry
identity), because the app is multi-server by design. Choosing an explicit
override suppresses the in-context upgrade offer permanently for that server.

When a farm grows past a threshold (a second account, a second bay, or shift
planning switched on after having been off), the Oversight tab root shows a
one-time, dismissible **upgrade offer card**: *"Your farm grew — Oversight can
become its own mode..."*. It is never a modal, never a toast, and never part
of onboarding. The app never auto-switches shells; changing shells always
requires an explicit user action.

Automatic therefore **latches** the layout it settles on for a server, and the
latch is persisted alongside the per-server preference. Once Automatic has
settled on Simple, later farm growth can only raise the upgrade offer — it can
never move the app to Two modes on a subsequent launch, and declining the offer
with **Not now** survives relaunch. The latch is one-directional: a derivation
that lands on Simple (shift planning switched off, the signed-in user is not a
`farm_admin`, the farm shape is unknown, or the farm shrank back below every
threshold) still applies immediately, because those are explicit negative
signals rather than growth. Choosing an explicit **Simple** or **Two modes**
override clears the latch, so returning to Automatic re-derives from the
server's current shape.

### iPad Layout

On iPad, the app uses a `NavigationSplitView`. Server switching lives in the
sidebar and the destination list is scoped to the operator set for the
active server.

### Advanced Printer Controls

Advanced printer controls are off by default for every server. To use jog,
preheat, home, z-offset, or disable motors, open **Settings** → **Printer
Safety** and enable **Advanced Printer Controls** for the active server.
Enabling the controls on one server does not enable them on another. Turning
the setting off removes access immediately, including an open advanced-controls
screen. Changing a registered server's URL also resets the setting to off so an
opt-in cannot carry over to a different endpoint. Misuse may damage a printer or
ruin a print.

### Post-Login Connection Check

After sign-in or session restoration, the app checks each enabled mobile backend
feature before opening the main interface. If one or more services are
unavailable, the app names them in an alert and lets you continue with cached
data and any services that remain available. Features disabled by the server's
`operatorFeatures` capability flags are omitted from navigation and views rather
than shown as unavailable placeholders. For up to 30 seconds, promptly completed
canonical checks hand their confirmed-live Attention feed, fleet filament
coverage, and printer list to the first tab activation, avoiding a duplicate
startup fetch and stale-cache banner. Attention's original lightweight readiness
request runs concurrently and solely determines availability; canonical warming
is best-effort and capped at one second. Tabs without a handoff perform their
normal fresh load.

### HTTPS Certificate Trust

Public servers require HTTPS with normal system certificate trust. Cleartext
HTTP remains available only for local-network addresses and names such as
`localhost` and `.local`.

For a private HTTPS server using a self-signed certificate, the app pauses the
first connection and asks you to verify its SHA-256 public-key fingerprint
before sending credentials. Compare the displayed value with one obtained
directly from the server:

```bash
openssl x509 -in cert.pem -pubkey -noout \
  | openssl pkey -pubin -outform der \
  | openssl dgst -sha256
```

The certificate must contain a subject alternative name matching the connected
host; certificates used with an IP address need that address as an IP SAN.
Confirmed pins are device-only. If a certificate key changes, the app blocks
the connection. After independently verifying an intentional replacement, open
**Settings** → **Manage Servers**, edit the server, and choose **Forget Trusted
Certificate** before reconnecting.

### Development URL Seeding

`PRINTFARMER_API_URL` is now a development seed/override for the server registry,
not the only server the app can use. Set it in the Xcode scheme when you want a
simulator or device run to start with a specific backend, such as the local .NET
API:

```bash
PRINTFARMER_API_URL=http://localhost:5245
```

Existing installs that saved a single legacy URL under `pf_server_url` migrate
that URL into the server registry on first launch and make it the initial active
server.

## TestFlight Betas

The iOS beta line is authoritative for TestFlight versions. Use `v1.0-beta.N`
tags for beta releases; the repo-root `VERSION` file is not used to derive iOS
beta marketing versions.

To cut an on-demand internal beta from GitHub Actions:

```bash
gh workflow run testflight-beta.yml -f environment=internal
```

The workflow creates and pushes the next `v1.0-beta.N` tag from the latest beta
tag series unless `marketing_version` or `beta_number` inputs are supplied.

The canonical tag-based method is:

```bash
git tag -a v1.0-beta.<N> -m "PrintFarmer iOS beta v1.0-beta.<N>"
git push origin v1.0-beta.<N>
```

## License

The in-repository mobile client is licensed under the
[GNU Affero General Public License v3.0 only](../LICENSE)
(`AGPL-3.0-only`) beginning with PrintFarmer v0.2.3.
