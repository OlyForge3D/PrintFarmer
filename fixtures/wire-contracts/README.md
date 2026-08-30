# Wire-contract corpus

Canonical, checked-in corpus of serialized wire payloads produced by **real production
serialization** — never hand-built CLR objects. See issue #2238 (parent epic #2237) for the
full rationale: a React test mock once asserted a hand-written client DTO shape instead of what
the server actually serializes (#2232), and stayed green while the two drifted apart. This
corpus exists so every consumer (.NET, the React app under `src/Web/ReactApp/`, and the iOS app
under `mobile/`) can test against one ground truth instead of a client-side assumption.

## Location rationale

This directory lives at the **repository root** (a sibling of `src/` and `mobile/`) because it
is the only location reachable by a short relative path from all three consumers without nesting
one consumer's tree inside another's:

- .NET: `src/tests/Farm.Testing.Shared/WireContractCorpusPaths.cs` resolves this directory by
  walking up from the test assembly to the repo root.
- Vitest: `src/Web/ReactApp/` can reach it via `../../../fixtures/wire-contracts`.
- Xcode: `mobile/` can reach it via `../fixtures/wire-contracts`.

This path was also recorded as a comment on epic #2237.

## Layout

```
fixtures/wire-contracts/
  manifest.json                      -- provenance registry (see below)
  api/<family>/<variant>.json        -- PrintFarmer DTOs: camelCase, string enums
  native-slicer/<family>/<variant>.json -- OrcaSlicer native payloads: snake_case
```

`api/` and `native-slicer/` are **never merged**. A shared normalization helper across that
boundary is a defect in this corpus, not a convenience — file it as its own issue rather than
adding one.

## Fixture naming

Each payload family gets one file per variant covering, where applicable to that payload:
`minimal`, `missing-key`, `empty-collection`, `populated`, `unknown-additive-field`, and one
file per public enum showing its exact string token.

An `explicit-null` variant is added only for the rare payload shape whose serializer options
do *not* apply the project's global `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`
policy. For the overwhelming majority of nullable optional properties in this corpus, that
global policy means a `null` value is OMITTED entirely — it never reaches the wire as an
explicit JSON `null` — so the correct, and only meaningful, variant for those fields is
`missing-key`, not `explicit-null`. Do not add an `explicit-null` fixture for a field that the
policy would only ever omit; that variant name is reserved for genuine explicit-null cases.

## Provenance (`manifest.json`)

A flat JSON array, one entry per fixture file:

```json
{
  "path": "api/printers/status.minimal.json",
  "endpoint": "GET /api/printers/{id}/status",
  "producingTest": "Farm.Web.Api.Tests.Contracts.PrinterStatusContractTests.GetStatus_Minimal_MatchesCorpus",
  "schemaVersion": "1",
  "refreshCommit": "<git sha at last refresh>"
}
```

## How fixtures are generated and kept honest

Fixtures are **not** hand-written. They are produced by
`Farm.Testing.Shared.WireContractFixtureWriter.CaptureOrVerifyAsync`, called from a contract
test after making a real HTTP/SignalR call against a `CustomWebApplicationFactory`-hosted app
(or, for the native corpus, real OrcaSlicer profile-parsing code):

- If the fixture file does not yet exist, or the `WIRE_CONTRACT_REGEN=1` environment variable is
  set, the helper **writes** the real serialized JSON to disk and records/updates its
  `manifest.json` entry. This is the only path that authors or refreshes a fixture, and it is
  never invoked by CI.
- Otherwise (the normal path, every CI run) the helper **verifies**: it re-serializes the real
  payload and structurally compares it against the checked-in file via
  `JsonContractAssertions.AssertStructurallyEqual`. Any difference — a renamed property, a
  numeric enum where a string token is expected, a null-handling change — fails the test and
  turns the owning CI leg red. That is the corpus's actual protection: it is a live regression
  guard, not a one-time snapshot.

## Consuming this corpus

Every consumer other than the tests listed above (owned by issue #2238) reads this directory
**read-only**. Do not add a normalizing/adapter layer that merges `api/` and `native-slicer/`
semantics, and do not hand-edit a fixture file — regenerate it via the owning test with
`WIRE_CONTRACT_REGEN=1` instead, so the provenance manifest stays accurate.
