# iOS operator-first beta release checklist (#724)

Owner: Parker (DevOps/CI/CD). This is the release/CI readiness gate for epic
[#705](https://github.com/OlyForge3D/PrintFarmer/issues/705). It complements,
and does not replace, the epic's own
["Explicit beta-release gate"](https://github.com/OlyForge3D/PrintFarmer/issues/705)
checklist and QA's independent qualification in
[#723](https://github.com/OlyForge3D/PrintFarmer/issues/723).

**This document does not authorize a beta.** Triggering `testflight-beta.yml`
(tag push or `workflow_dispatch`) is a manual, human action gated on every item
below plus Jeff's explicit go-ahead. Nothing in this checklist, and no CI job,
tags or dispatches a beta automatically.

## 1. Dependency gate (informational — verify at trigger time, not merge time)

| Dependency | Status at authoring time | Re-verify before triggering |
|---|---|---|
| [#708](https://github.com/OlyForge3D/PrintFarmer/issues/708) optional APNs backend capability | Closed — backend capability merged; shipped v1 remains disabled | Confirm no reopen |
| F1–F10 (#706–#715) implementation | All closed | Confirm no reopen |
| Required React follow-ups (#716–#722) | All closed | Confirm no reopen |
| [#723](https://github.com/OlyForge3D/PrintFarmer/issues/723) QA beta qualification (Kane) | Open, in progress in a parallel session | **Must be closed with no open P0/P1 before trigger** |
| [#724](https://github.com/OlyForge3D/PrintFarmer/issues/724) this issue | In progress | Must be closed |
| Bishop/Hicks/Vasquez unanimous approval on every release-bound PR | Required per PR | Re-verify per `squad/pre-pr-verdict` status, not by memory. This is a self-attested agent review record, not independent approval — see `.github/copilot-instructions.md` |
| Jeff's explicit release-execution request | Not yet given | **Hard stop until given** |

## 2. Alerts and notification configuration

Architecture reference: [`docs/OPERATOR_NATIVE_PUSH.md`](./OPERATOR_NATIVE_PUSH.md).

- [ ] Confirm `NativePush__Mode=disabled` for the shipped v1 App Store
  deployment. OlyForge3D does not operate a backend or notification relay.
- [ ] Confirm live in-app status updates arrive directly from the user-selected
  self-hosted server over SignalR.
- [ ] Confirm the only shipped v1 system-notification path is the on-device
  `PendingReadyMonitor` scheduling a `PENDING_READY` / **Bed Clear Required**
  reminder; there is no `JOB_ATTENTION` category or action handler.
- [ ] Confirm the iOS client does not call
  `POST/DELETE /api/notifications/device-tokens`.
- [ ] Confirm no `NativePush__Relay__*` or `NativePush__Apns__*` credentials are
  required or provisioned for the shipped v1 release.
- [ ] `NativePushSettingsValidator` startup validation passes in the target
  environment with disabled mode.
- [ ] Emergency kill switch documented and rehearsed:
  `OperatorFeatures__nativePushEnabled=false` wins over the DB-backed flag.
- [ ] `GET /api/system/capabilities` reports native push disabled.

## 3. iOS entitlements, bundle ID, and signing

- [ ] `mobile/PrintFarmer/PrintFarmer.entitlements` does not declare
  `aps-environment`, and the app target has no `APS_ENVIRONMENT` build setting.
  Local notifications do not require the APNs entitlement.
- [ ] Bundle identifier is `com.olyforge3d.printfarmer.ios` in
  `PRODUCT_BUNDLE_IDENTIFIER` (both Debug/Release app-target configs) and
  matches `ExportOptions.plist` and `Matchfile`'s `app_identifier`.
- [ ] `DEVELOPMENT_TEAM = ZPKA84F3TY` matches `ExportOptions.plist`'s
  `teamID` and the `fastlane match` team.
- [ ] TestFlight signing: `testflight-beta.yml` uses
  `CODE_SIGN_STYLE=Manual` with `fastlane match appstore --readonly` and the
  `iPhone Distribution` identity — confirm the required secrets
  (`MATCH_PASSWORD`, `MATCH_GIT_URL`, `MATCH_GIT_TOKEN`,
  `APP_STORE_CONNECT_API_KEY_ID`, `APP_STORE_CONNECT_API_ISSUER_ID`,
  `APP_STORE_CONNECT_API_KEY_CONTENT`) are present in repository secrets
  before dispatching.
- [ ] `./scripts/verify-marketing-version.sh` passes for the target tag (CI
  already gates this in both `ios-pr-ci.yml` and `testflight-beta.yml`).

## 4. Server configuration and health checks

- [ ] Deployment docs (`docs/OPERATOR_NATIVE_PUSH.md` §8, `.env.template`)
  are current for the chosen topology.
- [ ] `/healthz` and `/health` report healthy with the target `NativePush`
  configuration loaded (a misconfigured non-disabled mode fails startup
  validation, so a healthy process start is itself a signal).
- [ ] Confirm the shipped deployment does not require outbound egress to an
  APNs provider or notification relay.

## 5. CI coverage

- [ ] `ios-pr-ci.yml` is green on the release-bound branch: Xcode build,
  unit tests (`PrintFarmerTests`), and the iPhone/iPad XCUI shard-one
  accessibility-XXXL coverage.
- [ ] `ci.yml` (backend `dotnet test` + React `npm run test:run`/lint) is
  green for the same commit range — this issue does not duplicate that
  suite, it confirms the existing gate covered the merged native-push and
  operator-redesign changes.
- [ ] `squad-review-verdict.yml` verified for every release-bound PR via:
  ```bash
  node scripts/ci/verify-squad-verdict.mjs --repo OlyForge3D/PrintFarmer --pr <number> --json
  ```
  No `SUPERSEDED` or missing records on the exact head SHA being released.

## 6. Trigger procedure (only after every section above is checked)

1. Confirm #723 is closed with no open P0/P1 defects.
2. Confirm Jeff has explicitly requested release execution after reviewing
   this checklist and the epic's gate.
3. Dispatch `testflight-beta.yml` (tag `ios/v*-beta.N` push, or
   `workflow_dispatch` with `environment=internal` for the first beta ring).
4. After upload succeeds, verify the TestFlight build appears in App Store
   Connect and the auto-created GitHub Release is `prerelease: true`.
5. Smoke-test SignalR-driven in-app updates and the `PendingReady` bed-clear
   local notification before widening distribution to `external` groups.

## 7. Disable/rollback controls summary

| Control | Mechanism | Scope |
|---|---|---|
| Native push kill switch | `OperatorFeatures__nativePushEnabled=false` (env, wins over DB) or Unified Settings toggle (DB, hot-reloadable) | Stops queueing/sending immediately; registration returns `404 featureDisabled`; tokens retained |
| Offline write replay kill switch | `OperatorFeatures__offlineWriteReplayEnabled=false` | Disables idempotent write-queue replay per [`docs/OPERATOR_FEATURE_GATES.md`](./OPERATOR_FEATURE_GATES.md) |
| Attention feed kill switch | `OperatorFeatures__attentionEnabled=false` | Disables the unified attention pipeline that triggers push |
| TestFlight build pull | Remove/expire the build in App Store Connect | Stops new installs/updates; does not revoke already-installed builds |

See [`docs/OPERATOR_FEATURE_GATES.md`](./OPERATOR_FEATURE_GATES.md) for the
full flag contract and [`docs/OFFLINE_WRITE_REPLAY.md`](./OFFLINE_WRITE_REPLAY.md)
for the write-queue rollback story.
