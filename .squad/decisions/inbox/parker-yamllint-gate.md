### 2026-08-26: Make yamllint fail closed with a pragmatic workflow policy
**By:** Parker
**What:** Run yamllint on every pull request, preserve its real exit status through
artifact upload, and fail explicitly afterward. Use a checked-in policy that disables
`document-start`, excludes mapping keys from `truthy`, reports lines over 120 characters
as warnings, and promotes comment spacing to an error. Pin workflow YAML to LF in
`.gitattributes`.
**Why:** The previous `main` branch filter skipped the repository's `development` PR
flow, while `|| true` made every captured status zero. GitHub Actions expressions and
shell commands are frequently unsafe to wrap, but structural YAML, trailing whitespace,
bracket spacing, and comment spacing remain low-risk blocking checks. A workflow-scoped
LF attribute prevents Windows checkouts from reintroducing tracked CRLF blobs.
