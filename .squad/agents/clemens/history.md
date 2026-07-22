# Clemens History

## 2026-07-21 — Joined for #816 clean-room revision

- Assigned the fourth clean-room #816 rebuild after `2fb0c86b` was unanimously rejected.
- Must start from feature tip `00bef23c` and avoid all rejected #785/#816 commits, worktrees, patches, advice, tests, logs, and evidence.
- Locked authors: Gorman, Hudson, Anvil, Kane, Dallas, Frost, Dietrich, Apone, Crowe, and Morse.
- Drake remains the distinct #817 author; Clemens owns only the offline-capable foundation and directly coupled auth/registry/removal integration.
- Binding consensus blocker: authenticated identity must remain pinned to its source server across pending registry switches.
- Required verified hardening: stable offline owner identity, persisted-demo exit activation, failure-atomic rejected writes under double faults, post-move authority fencing, incoming schema validation, compiler-enforced purge-only removal, and real production-order tests.
