# Parker — Work History

**Note:** Full history archived in `history-archive.md`. This file shows recent work only.

## 2026-08-26: YAML Linting Governance Decision

**Summary**: Established pragmatic yamllint workflow policy for CI/CD.

**Decision Details**: Run yamllint on every PR, preserve exit status through artifact upload, and fail explicitly. Workflow includes checked-in policy disabling `document-start`, exempting mapping keys from `truthy`, reporting lines over 120 characters as warnings, and promoting comment spacing violations to errors. Pin workflow YAML to LF in `.gitattributes`.

**Rationale**: Previous main branch filter skipped development PR flow while `|| true` masked failures. GitHub Actions expressions are unsafe to wrap, but structural YAML, trailing whitespace, bracket spacing, and comment spacing are low-risk blocking checks. Workflow-scoped LF attribute prevents Windows checkouts from reintroducing tracked CRLF blobs.

**Status**: COMPLETE
