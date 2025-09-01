# Analyzer hardening: adopt LoggerMessage, tighten catches, dispose correctly, and use Uri overloads

Status: Open (tracking)
Owner: @jpapiez
Related: Directory.Build.props analyzers; .editorconfig temporary suppressions

## Summary
Reduce analyzer noise temporarily to focus on code hardening. Then remove suppressions and fix sources.

## Scope
- Logging: replace LoggerExtensions.LogX with compiled LoggerMessage patterns.
- Exceptions: replace catch (Exception) with specific exceptions; avoid swallowing; include context.
- Disposables: ensure using/await using; follow dispose patterns; fix IDISP00x and CA2000.
- HttpClient: prefer Uri-based APIs; construct Uri once; pass CancellationToken through.

## Tasks
- NetworkDiscoveryService: LoggerMessage, specific catches.
- MoonrakerClient: Uri overloads; using blocks; remove ignored disposables; specific catches.
- PrusaLinkApiClient/Extensions: Uri overloads; dispose HttpRequestMessage; specific catches.
- SdcpClient: Uri overloads; dispose content/requests; fix Dispose pattern.
- PrintersController: reduce catch-all; pass ct; improve culture usage; standardize logging.
- GcodeHarvestService/HarvestWorkerService/MoonrakerSubscriptionService: LoggerMessage; catch specificity; VSTHRD guidance; dispose.

## Definition of Done
- Build with warnings as errors (excluding Nullable) passes without temporary suppressions.
- .editorconfig temporary suppression block removed.
- New tests added for error paths where applicable.

## Plan
1) Add LoggerMessage static partials to hot paths (NetworkDiscoveryService, MoonrakerSubscriptionService, MoonrakerClient).
2) Replace catch (Exception) with targeted exceptions or rethrow with context.
3) Ensure proper disposing (using declarations/await using) and fix IDISP warnings.
4) Convert string-based HttpClient calls to Uri-based overloads.
5) Re-enable analyzers: remove .editorconfig block; restore stricter severities.

## Risks
- Behavior changes in error handling/logging; validate with integration tests.
- Uri migration might change behavior for malformed inputs; add tests.

## Tracking
- This document acts as an interim GitHub Issue. When ready, port to a real issue and link PRs.
