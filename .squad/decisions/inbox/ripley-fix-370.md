# Decision: Kasa TCP DoS hardening (PR #370)

**Author:** Ripley (lockout rule — Lambert locked out)
**Date:** 2025-07-25
**Context:** Hicks round 2 review identified unbounded allocation from Kasa device TCP length prefix.

## Changes

1. **Max response size cap (64KB)** — `SendKasaCommandAsync` rejects length prefixes ≤ 0 or > 65,536 bytes with `InvalidOperationException`.
2. **Read timeout (5s)** — A linked `CancellationTokenSource` with `CancelAfter(5s)` prevents hung sockets from blocking the polling thread indefinitely.
3. **Port parsing** — `deviceAddress` now supports optional `:port` suffix (defaults to 9999). This enables loopback testing without modifying constants.
4. **Tests** — `KasaSmartPlugProviderTests` covers oversized length, negative length, and read timeout scenarios using a real TCP listener on loopback.

## Rationale

A compromised or malformed Kasa device could send a 4-byte length header claiming a multi-GB payload. Without bounds checking, `new byte[len]` would OOM the process. The 64KB cap is generous — real Kasa emeter JSON responses are < 1KB.
