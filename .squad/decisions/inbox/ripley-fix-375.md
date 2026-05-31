## Ripley fix on PR #375 — URL substring vulnerability

**Date:** 2025-07-25
**Author:** Ripley (frontend, acting under lockout rule for Lambert)
**PR:** #375

### Decision

Hardened `PrintablesImportService.ParseModelId` against substring domain spoofing by:

1. Using `System.Uri` constructor to parse the URL and extract the host
2. Validating host against an exact-match allowlist (`printables.com`, `www.printables.com`)
3. Applying an anchored regex (`^/model/(\d+)`) only on the path component

### Rationale

The original unanchored regex `printables\.com/model/(\d+)` matched substring occurrences, meaning
`fakeprintables.com/model/123` or `printables.com.evil.org/model/123` would pass validation.
Using `System.Uri` for host extraction is the standard defense against URL parsing ambiguities
(userinfo attacks, subdomain tricks, etc.).

### Impact

- Security fix only — no API contract changes
- 5 new negative test cases added for spoofing vectors
