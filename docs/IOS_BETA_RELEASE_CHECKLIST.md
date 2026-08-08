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
| [#708](https://github.com/OlyForge3D/PrintFarmer/issues/708) APNs topology decision | Closed — relay topology selected, backend merged (PR #750, #758) | Confirm no reopen |
| F1–F10 (#706–#715) implementation | All closed | Confirm no reopen |
| Required React follow-ups (#716–#722) | All closed | Confirm no reopen |
| [#723](https://github.com/OlyForge3D/PrintFarmer/issues/723) QA beta qualification (Kane) | Open, in progress in a parallel session | **Must be closed with no open P0/P1 before trigger** |
| [#724](https://github.com/OlyForge3D/PrintFarmer/issues/724) this issue | In progress | Must be closed |
| Bishop/Hicks/Vasquez unanimous approval on every release-bound PR | Required per PR | Re-verify per `squad/pre-pr-verdict` status, not by memory. This is a self-attested agent review record, not independent approval — see `.github/copilot-instructions.md` |
| Jeff's explicit release-execution request | Not yet given | **Hard stop until given** |

## 2. APNs / native push configuration

Architecture reference: [`docs/OPERATOR_NATIVE_PUSH.md`](./OPERATOR_NATIVE_PUSH.md).

- [ ] Confirm target topology for the beta: `NativePush__Mode=relay`
  (recommended; OlyForge3D never distributes its `.p8` to self-hosted
  installs) vs `direct` for an internal/enterprise-signed build. Do not mix —
  pick one per environment.
- [ ] Relay mode: `NativePush__Relay__Endpoint` and
  `NativePush__Relay__ApiKey` are set as deployment secrets (env var, mounted
  secret, or orchestrator secret store) — **never** in `.env`, compose files,
  or source control. `.env.template` documents the keys with no live values;
  diff any deployment `.env` against it before shipping to confirm no real
  secret was accidentally committed.
- [ ] Direct mode (if used): the `.p8` is P-256 ECDSA, mounted read-only
  (`NativePush__Apns__P8KeyPath`) or injected as an inline PEM via secret
  manager (`NativePush__Apns__P8KeyPem`); `TeamId`, `KeyId`, `BundleId`, and
  `Environment` (`development`/`production`) match the target App ID exactly.
- [ ] `NativePushSettingsValidator` startup validation passes in the target
  environment (`ValidateOnStart()` fails fast on bad config — a clean pod/
  container start is itself evidence).
- [ ] Emergency kill switch documented and rehearsed:
  `OperatorFeatures__nativePushEnabled=false` wins over the DB-backed flag.
- [ ] `GET /api/system/capabilities` reflects the expected `nativePushEnabled`
  value in the target environment.

### Rollback rehearsal (run in staging before beta trigger)

Per Dallas's #708 acceptance addendum, rehearse the full disable/enable cycle
in staging and record the result here or in the release run notes:

1. Disable: flip `nativePushEnabled` off (Unified Settings or
   `OperatorFeatures__nativePushEnabled=false` + restart).
2. Create an attention-triggering event (e.g. a simulated print failure).
3. Verify **zero** relay/APNs calls were made (check
   `NativePushDirect`/`NativePushRelay` HTTP client telemetry and sender
   logs) and that `POST/DELETE /api/notifications/device-tokens` returns
   `404 ProblemDetails{code="featureDisabled"}`.
4. Re-enable the flag.
5. Verify delivery resumes for a new attention event **without requiring
   re-registration** (existing `DeviceToken` rows are retained across the
   disable window).

Record pass/fail here before proceeding:

- [ ] Disable → zero provider calls confirmed
- [ ] Re-enable → delivery resumes without re-registration

## 3. iOS entitlements, bundle ID, APNs environment, signing

- [ ] `mobile/PrintFarmer/PrintFarmer.entitlements` declares
  `aps-environment` (via the `APS_ENVIRONMENT` build setting: `development`
  for Debug, `production` for Release/App Store archives) alongside the
  existing NFC entitlement. Verify with:
  ```bash
  /usr/libexec/PlistBuddy -c "Print :aps-environment" \
    build/PrintFarmer.xcarchive/Products/Applications/PrintFarmer.app/PrintFarmer.app.dSYM/../embedded.mobileprovision 2>/dev/null || \
  codesign -d --entitlements :- build/PrintFarmer.xcarchive/Products/Applications/PrintFarmer.app | grep -A1 aps-environment
  ```
  Expect `production` for a TestFlight/App Store archive.
- [ ] Bundle identifier is `com.olyforge3d.printfarmer.ios` in
  `PRODUCT_BUNDLE_IDENTIFIER` (both Debug/Release app-target configs) and
  matches `ExportOptions.plist` and `Matchfile`'s `app_identifier`.
- [ ] Apple Developer Portal App ID `com.olyforge3d.printfarmer.ios` has the
  **Push Notifications** capability enabled (portal-side; not tracked in this
  repo) so the provisioning profile pulled by `fastlane match` actually
  authorizes push. A build can compile and archive successfully with the
  entitlement present in source yet still be rejected/silently non-functional
  for push if the portal capability or profile is stale — re-run
  `fastlane match appstore` (not `--readonly`) after enabling the capability
  so `PrintFarmerApp-certificates` picks up a regenerated profile.
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

## 4. Server configuration and health checks for push delivery

- [ ] Deployment docs (`docs/OPERATOR_NATIVE_PUSH.md` §8, `.env.template`)
  are current for the chosen topology.
- [ ] `/healthz` and `/health` report healthy with the target `NativePush`
  configuration loaded (a misconfigured non-disabled mode fails startup
  validation, so a healthy process start is itself a signal).
- [ ] Confirm outbound network egress to the selected APNs/relay host is
  permitted from the deployment environment (firewall/NAT allowlist for
  `api.push.apple.com` / `api.sandbox.push.apple.com` in direct mode, or the
  configured relay hostname).

## 5. CI coverage

- [ ] `ios-pr-ci.yml` is green on the release-bound branch: Xcode build,
  unit tests (`PrintFarmerTests`), and Attention XCUI accessibility-XXXL
  matrix (iPhone + iPad).
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
3. Dispatch `testflight-beta.yml` (tag `v*-beta.N` push, or
   `workflow_dispatch` with `environment=internal` for the first beta ring).
4. After upload succeeds, verify the TestFlight build appears in App Store
   Connect and the auto-created GitHub Release is `prerelease: true`.
5. Smoke-test lock-screen actionable notifications end-to-end against the
   production relay before widening distribution to `external` groups.

## 7. Disable/rollback controls summary

| Control | Mechanism | Scope |
|---|---|---|
| Native push kill switch | `OperatorFeatures__nativePushEnabled=false` (env, wins over DB) or Unified Settings toggle (DB, hot-reloadable) | Stops queueing/sending immediately; registration returns `404 featureDisabled`; tokens retained |
| Offline write replay kill switch | `OperatorFeatures__offlineWriteReplayEnabled=false` | Disables idempotent write-queue replay per [`docs/OPERATOR_FEATURE_GATES.md`](./OPERATOR_FEATURE_GATES.md) |
| Attention feed kill switch | `OperatorFeatures__attentionEnabled=false` | Disables the unified attention pipeline that triggers push |
| TestFlight build pull | Remove/expire the build in App Store Connect | Stops new installs/updates; does not revoke already-installed builds |
| Relay credential revoke | Rotate/revoke `NativePush__Relay__ApiKey` at the relay | Immediate hard-stop for relay-mode delivery, independent of app-side flags |

See [`docs/OPERATOR_FEATURE_GATES.md`](./OPERATOR_FEATURE_GATES.md) for the
full flag contract and [`docs/OFFLINE_WRITE_REPLAY.md`](./OFFLINE_WRITE_REPLAY.md)
for the write-queue rollback story.
