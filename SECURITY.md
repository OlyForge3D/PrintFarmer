# Security Policy

## Supported Versions

Only the latest commit on `main` is actively supported. Release tags (`vX.Y.Z`) receive fixes on a best-effort basis.

## Reporting a Vulnerability

1. **Do not open a public issue** for an undisclosed vulnerability.
2. Email: `security@placeholder.local` (replace with a real address) including:
   - Affected component (API / Frontend / Docker image)
   - Version or commit SHA
   - Vulnerability description & impact
   - Steps to reproduce / PoC
3. Expect initial acknowledgment within 5 business days.
4. Coordinated disclosure: We aim to release a fix before public disclosure.

## Handling Process

| Phase | Target | Notes |
|-------|--------|-------|
| Triage | 5 days | Confirm severity & scope |
| Fix Development | 10 days | May request clarifications |
| Release | 5 days | Publish patch & release notes |
| Disclosure | +2 days | CVE assignment if applicable |

## Security Tooling

- CI image scanning: Trivy & Grype (CRITICAL/HIGH surfaced)
- SBOM: Syft (SPDX JSON) + Docker build SBOM
- Provenance: SLSA generator (attestations on successful builds)

## Hardening Roadmap

- Enforce vulnerability fail thresholds (pending baseline)
- Runtime security policies (Seccomp/AppArmor) in Docker examples
- Signed container images (cosign) & signature verification in deployment

## Contact

After responsible disclosure & fix publication, issues may be publicly referenced in release notes. For urgent matters use the reporting email with "URGENT" in subject.
