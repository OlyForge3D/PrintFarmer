# Operator Feature Gates

Shared, named enable/disable mechanism for operator-first mobile features (epic
[#705](https://github.com/OlyForge3D/PFarm/issues/705), implemented by
[#725](https://github.com/OlyForge3D/PFarm/issues/725)). This is the **only**
sanctioned way to disable or roll back the features in issues
[#707](https://github.com/OlyForge3D/PFarm/issues/707)–[#715](https://github.com/OlyForge3D/PFarm/issues/715);
feature implementations must consume `IOperatorFeatureGate` rather than
inventing per-feature booleans.

## Section, keys, and defaults

Settings class: `Farm.Infrastructure.Settings.OperatorFeatureSettings`.
Configuration/section key: `OperatorFeatures`.
Effective flags are exposed on the wire in camelCase.

| Flag (JSON) | Default | Owner issue | Purpose |
|---|---|---|---|
| `attentionEnabled` | `true` | [#707](https://github.com/OlyForge3D/PFarm/issues/707) | Unified attention/exception feed and typed action endpoints. |
| `nativePushEnabled` | `false` | [#708](https://github.com/OlyForge3D/PFarm/issues/708) | APNs registration and delivery for operator alerts. Off until a provider/relay is configured. |
| `filamentCoverageEnabled` | `true` | [#709](https://github.com/OlyForge3D/PFarm/issues/709) | Coverage/runout calculations exposed to clients. |
| `guidedSwapEnabled` | `true` | [#710](https://github.com/OlyForge3D/PFarm/issues/710) | Per-tool requirements, swap validation, and guided swap flow. |
| `multiSlotFallbackEnabled` | `true` | [#711](https://github.com/OlyForge3D/PFarm/issues/711) | Fallback groups, per-tool maintenance, dispatch loadout. |
| `shiftPlanEnabled` | `true` | [#713](https://github.com/OlyForge3D/PFarm/issues/713) | Shift compiler and Tasks feed. |
| `printedPartsInventoryEnabled` | `true` | [#714](https://github.com/OlyForge3D/PFarm/issues/714) | Printed-part stock, bins, harvest, scan/inventory API. |
| `offlineWriteReplayEnabled` | `true` | [#715](https://github.com/OlyForge3D/PFarm/issues/715) | Idempotent write queue and offline replay. |

## Resolution order

For each feature, the effective value is computed per request as:

1. If an ASP.NET configuration/environment value named
   `OperatorFeatures:<flagName>` (or `OperatorFeatures__<flagName>` in
   environment form) is present **and parses as `false`**, the feature is
   hard-disabled regardless of the database value.
2. Otherwise, the runtime database value from `OperatorFeatureSettings`
   (persisted via the Unified Settings page and `SettingsService`) is used.

Absent, non-boolean, or explicitly `true` environment values do **not**
force-enable a feature — only explicit `false` acts as an override.

Runtime database changes take effect on the very next request because
`IOperatorFeatureGate` is scoped and re-reads `ISettingsService` on every call.

## Client-facing surface

`GET /api/system/capabilities` includes the resolved flags under
`operatorFeatures`:

```json
{
  "architecture": "X64",
  "slicingEnabled": true,
  "modelFilesEnabled": true,
  "thumbnailGenerationEnabled": true,
  "gcodeUploadEnabled": true,
  "platformNote": null,
  "operatorFeatures": {
    "attentionEnabled": true,
    "nativePushEnabled": false,
    "filamentCoverageEnabled": true,
    "guidedSwapEnabled": true,
    "multiSlotFallbackEnabled": true,
    "shiftPlanEnabled": true,
    "printedPartsInventoryEnabled": true,
    "offlineWriteReplayEnabled": true
  }
}
```

React and iOS clients **must** tolerate an older/newer server that omits any
flag (or the whole `operatorFeatures` object) and fall back to the defaults
in the table above.

## Consumer contract for gated HTTP endpoints

Every backend endpoint that implements a feature from #707–#715 must:

1. Inject `IOperatorFeatureGate`.
2. Check `IsEnabled(...)` at the top of the action.
3. Return `NotFound` with the shared ProblemDetails helper when disabled —
   before any DB write, cache mutation, or SignalR broadcast.

```csharp
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Infrastructure.OperatorFeatures;

[HttpPost("attention/{id:guid}/actions")]
public IActionResult ExecuteAction(Guid id, ActionRequest body)
{
    if (!_gate.IsEnabled(OperatorFeature.Attention))
    {
        return OperatorFeatureProblemDetails.NotFound(_gate, OperatorFeature.Attention);
    }

    // ...normal path...
}
```

The resulting response is HTTP 404 with a ProblemDetails body carrying two
extensions the frontend depends on:

```json
{
  "type": "https://printfarmer.io/errors/feature-disabled",
  "title": "Feature disabled",
  "status": 404,
  "detail": "The 'attentionEnabled' operator feature is disabled by an administrator.",
  "code": "featureDisabled",
  "feature": "attentionEnabled"
}
```

The `code: "featureDisabled"` extension is the stable machine identifier
clients use to render the disabled-feature affordance; `feature` is the
canonical camelCase flag name.

## SignalR and background services

For SignalR broadcasts and background workers, gate the emit/execute call the
same way:

```csharp
if (_gate.IsEnabled(OperatorFeature.PrintedPartsInventory))
{
    await _hub.Clients.All.SendAsync("inventoryupdated", payload, ct);
}
```

Lowercase SignalR event naming is unchanged; the gate only decides whether the
broadcast happens.

## Emergency rollback path

The environment hard-disable is intended for on-call use when a feature must
be turned off immediately and the Unified Settings page is unavailable or
cannot be trusted.

1. Set the ASP.NET configuration value in the deployment environment. Any of
   these forms work; use the one that matches how the process is launched:
   - Environment variable: `OperatorFeatures__attentionEnabled=false`
   - `appsettings.json` / overlay:
     ```json
     { "OperatorFeatures": { "attentionEnabled": false } }
     ```
   - Docker Compose (root `docker-compose.yml`):
     ```yaml
     services:
       api:
         environment:
           OperatorFeatures__attentionEnabled: "false"
     ```
2. Restart the API process (and slicer-host, if a slicer-side feature is
   gated). Environment overrides are picked up on configuration reload/restart;
   they are not hot-swappable.
3. Verify by curling `GET /api/system/capabilities` — the affected flag under
   `operatorFeatures` must be `false` and the on-call runbook update is done.
4. Once the underlying issue is resolved, remove the override, restart, and
   verify the capability endpoint reports `true` again.

The database value is left untouched by the environment override; clearing
the override restores the persisted state. This is the mechanism Kane's
RB-01–RB-05 qualification rows measure.

## Feature-level side-effect requirements

When disabled, each gated feature must preserve pre-existing user state so
re-enabling is safe:

- **Attention** (#707): the previous notification/source screens remain usable;
  no attention records or actions are created.
- **Native push** (#708): registered device tokens are kept so re-enabling
  resumes delivery without re-registration.
- **Filament coverage** (#709): the coverage/runout fields are omitted from
  responses; no cache is warmed.
- **Guided swap** (#710): swap validation reverts to the legacy path; no swap
  events are broadcast.
- **Multi-slot fallback** (#711): fallback-group evaluation is skipped;
  per-tool maintenance data is left as-is.
- **Shift plan** (#713): Tasks feed responds empty; no compiler runs.
- **Printed parts inventory** (#714): normal job completion is untouched; the
  scan/inventory endpoints return the shared 404 ProblemDetails; existing
  stock is preserved.
- **Offline write replay** (#715): clients fall back to direct-online
  mutations; queued entries are drained safely instead of being dropped.

## Testing

Unit tests live in `src/tests/Farm.Web.Api.Tests/Services/OperatorFeatures/`
and `src/tests/Farm.Web.Api.Tests/Controllers/SystemCapabilitiesControllerTests.cs`.
They cover:

- Default values match the issue specification.
- Runtime database values take effect on the next call.
- Environment `false` hard-disables, environment `true` does not force-enable,
  non-boolean values fall through.
- The capabilities controller exposes `operatorFeatures` in the response.
- The shared ProblemDetails helper produces `code: "featureDisabled"` and does
  not perform any writes or broadcasts.
- Older clients decoding a payload without `operatorFeatures` fall back to
  documented defaults.

RB-01 through RB-04 integration tests in feature PRs consume these exact
camelCase flag names.
