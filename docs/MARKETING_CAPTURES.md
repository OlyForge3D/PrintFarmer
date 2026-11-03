---
post_title: Marketing website and isolated captures
summary: Regenerate genuine frontend screenshots with synthetic data, without a backend or hardware.
---

## What is published

The marketing site is plain HTML and CSS in `site/`. The existing Pages workflow
publishes that directory; `site/privacy/` retains its existing URL. All assets use
relative paths so the site also works under `/PrintFarmer/`.

The three screenshot assets in `site/assets/captures/` show actual routed React
pages: Printers, Projects, and Filament Inventory's Spools tab. Names, jobs,
amounts, timestamps, account information, and service status are synthetic.
The images demonstrate frontend presentation, **not** functioning hardware,
backend safety, performance, or a verified Spoolman connection.

The logo is copied from `src/Web/ReactApp/public/printfarmer-logo.svg`.
The fleet uses eight existing OrcaSlicer model cover images through the app's
`PrinterImage` / `assetService` resolver and local asset manifest, not generic
fallbacks or screenshot edits. No customer photos or camera feeds are used.

## Models shown, connection evidence, and image rights

The fixture interprets "Elegoo CC1" as the original Centauri Carbon and "adx5"
as Flashforge AD5X. It selects the Trident 300 and V-Core 4 400 variants.
The table records source-level connection evidence, **not** verified operation
of these eight physical printers or every firmware/capability combination.

| Model shown | Fixture backend | Evidence and limits |
|---|---|---|
| Elegoo Centauri Carbon | SDCP | `SdcpClient` explicitly targets this model. |
| Snapmaker U1 | Moonraker | `MoonrakerDiscoveryProbe` and U1-specific client/subscription code handle its Moonraker-compatible interface. |
| Voron Trident 300 | Moonraker | Conditional on a reachable compatible Klipper/Moonraker installation; no Trident-specific hardware proof was collected. |
| Sovol SV08 | Moonraker | Conditional on its exposed compatible Moonraker API; no SV08-specific hardware proof was collected. |
| RatRig V-Core 4 400 | Moonraker | Conditional on a compatible Klipper/Moonraker installation; no V-Core 4-specific hardware proof was collected. |
| Flashforge AD5X | FlashForge | `FlashForgeBackendPlugin` explicitly notes AD5X's port 8080; `FlashForgeClient` parses its machine response. ZMOD/Moonraker is a separate firmware-dependent route, not used here. |
| Prusa MK4S | PrusaLink | Generic `PrusaLinkDiscoveryProbe` / `IPrusaLinkClient` connection route; exact model/firmware capability coverage was not verified here. |
| Prusa CORE One L | PrusaLink | Same generic PrusaLink route; the cover's presence does not establish CORE One L-specific hardware validation. |

Cover files are under `src/Web/ReactApp/public/assets/orcaslicer/`, with exact
manufacturer/model names recorded in `manifest.json`. The repository's
`scripts/restore-orcaslicer-assets.js` extracts model covers from OrcaSlicer
profile resources. The capture tests require the expected cover URL and a
decoded image for each card, with all eight cards fully visible in a 4x2 grid.

[Third-party notices](../THIRD-PARTY-NOTICES.md#orcaslicer) identify bundled
OrcaSlicer resources as AGPLv3 and require upstream notices to remain intact.
This is **not** per-image copyright provenance, marketing permission, or a
trademark endorsement. The exact upstream revision, individual image authors
and any separate vendor terms for these existing covers have not been
established by this capture work. Before public marketing publication, the
owner must resolve applicable attribution/source/notice obligations and any
separate image/trademark restrictions. No blanket clearance is asserted and
no public deployment is authorized by this change. Incorrect compatibility
claims could mislead buyers; incorrect rights assumptions could harm image
owners and expose the publisher to takedown or other claims. Local captures
can be replaced; public redistribution may not be fully reversible.

## Marketing surface design

The page keeps the established PrintFarmer navy-and-teal identity without
redefining the operational app's visual system. Oversized, tightly tracked
headings and concise copy lead into the fleet capture and setup actions. The
page then moves through an ownership statement, screenshot-backed feature
stories, setup steps, and a closing GitHub action. Screenshots use rectangular
plates, fine borders, and restrained depth.

The first-view fleet capture and feature captures show real routed React pages
rendered with synthetic local fixture data. The page labels and captions
disclose the sample data and state that there are no live farm or camera feeds.
These images demonstrate frontend presentation; they do not prove backend
behavior, hardware control, printer compatibility, or a connected Spoolman
installation.

At narrower widths, multi-column sections stack, the header can wrap, and the
footer navigation wraps; the page narrows its outer gutters for small screens.
Reduced-motion preferences disable the fleet-plate entrance animation,
transitions, and smooth scrolling. This direction applies only to `site/` and
does not replace the app-wide design system or imply live integrations.

## Regenerate and validate

Use the Node/npm versions required by the React app's `package.json`.
From `src/Web/ReactApp/`, install the locked dependencies if missing:

```powershell
npm ci
npx playwright install chromium
```

Run the dedicated configuration, **not** the general UI-validation configuration
(which starts a real API):

```powershell
npx playwright test --config e2e/marketing/playwright.config.ts
```

This command owns and stops its two loopback-only servers. It refuses to reuse
an existing server on ports 4178 or 4179. Successful capture tests overwrite:

- `site/assets/captures/fleet.png`
- `site/assets/captures/projects.png`
- `site/assets/captures/materials.png`

The site tests check asset decoding, keyboard skip navigation, anchors, privacy,
project-subpath behavior, reduced motion, and overflow at 320, 390, 768, and 1440
pixels. Desktop/mobile site previews are written to the ignored
`src/Web/ReactApp/test-results/marketing/` directory. They are review artifacts,
not Pages assets.

Type-check and lint only the harness:

```powershell
npx tsc --noEmit --target es2022 --module esnext --moduleResolution bundler --skipLibCheck e2e/marketing/fixtures.ts e2e/marketing/capture.spec.ts e2e/marketing/playwright.config.ts e2e/marketing/site.spec.ts
npx eslint e2e/marketing/*.ts e2e/marketing/*.mjs
```

For a persistent local **static-site** preview, run this separately after tests:

```powershell
node e2e/marketing/site-server.mjs
```

Open `http://127.0.0.1:4179/PrintFarmer/`. Stop with Ctrl+C. This is not a public
deployment and does not change Pages, DNS, permissions, or repository settings.

## Isolation contract

`e2e/marketing/server.mjs` starts Vite with no application Vite config, no `.env`
loading, no `VITE_*` environment values, no proxies, no API, and no backend
process. API and hub requests reaching this server directly return 503.

Each capture uses a new, nonpersistent browser context with service workers
blocked. The route guard fulfills only explicitly listed API method/path pairs.
Unknown API requests and all writes other than the listed synthetic SignalR
negotiations return 503, record a diagnostic, and fail the capture.
Other loopback ports and external requests are aborted before delivery.
The app's optional Google Fonts stylesheet is fulfilled with empty CSS locally;
no font is downloaded. System-font rendering can differ between operating
systems. Pin OS, locked Playwright/browser version, locale, timezone, viewport,
theme, and fixed clock for visual comparisons; this is reproducible scene data,
not a promise of cross-platform byte-identical PNGs.

All WebSockets are intercepted without connecting to a server. Listed hubs
receive an in-memory JSON protocol handshake and invocation completions; no
events come from a printer. The local Vite HMR socket is also terminated in
memory. Synthetic authentication storage exists only in that fresh browser
context; its deliberately invalid token is not accepted by a real server.
No production authentication or application runtime code is changed.

The entry reuses the existing `e2e/fixtures/toolbar-routed-app.mjs` pattern:
it mounts the real `App`, providers, routes, CSS, and components, skipping only
the normal entry's optional telemetry/service-worker bootstrap. Screens are
never rebuilt, and the capture harness never edits the rendered DOM.

Before saving, the tests assert the real route heading, populated demo records,
healthy mock connection state, and absence of loading/error alerts and uncaught
page errors. Negative probes verify that unknown APIs, commands, external
destinations, and port 5245 cannot fall through. All fixture code stays outside
`site/`; only reviewed static files are published.

## Changing a scene

Edit the explicit fixtures and assertions together. Printer, project, filament,
and queue-watermark data use the app's TypeScript contracts. Existing inventory
fixture data is reused for auxiliary catalog/Spoolman endpoints.
Do not add catch-all success responses, point the harness at a real server, load
saved browser profiles, or import customer exports to make a capture pass.

Use actual UI actions to select a view. Keep the synthetic-data disclosure on the
site. Open each regenerated full-size screenshot before publishing: a changed
route, missing record, loader, or error is a failed capture, not marketing art.
Review keyboard behavior and contrast manually as well; the focused browser
checks do not establish complete WCAG conformance.
