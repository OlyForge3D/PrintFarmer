---
name: release
description: Cut a new PrintFarmer release using the dual-history release script. Use when the user asks to cut, ship, or create a release (major, minor, or patch).
confidence: high
---

# PrintFarmer Release Skill

Use this skill whenever the user asks to cut a release, ship a version, or bump the version.

## Prerequisites

- **Clean working tree** — all changes must be committed and pushed before releasing.
- **On `development` branch** — the release script merges `development` → `main`.
- **Both remotes configured:**
  - `origin` → `https://github.com/jpapiez/PrintFarmer.git` (private, full history)
  - `release` → `https://github.com/OlyForge3D/PrintFarmer.git` (public, clean history)

## How to Release

From the **repo root** (`/Users/jpapiez/s/PFarm1`):

```bash
./scripts/release.sh patch    # v0.2.1 → v0.2.2
./scripts/release.sh minor    # v0.2.2 → v0.3.0
./scripts/release.sh major    # v0.3.0 → v1.0.0
./scripts/release.sh v1.2.3   # explicit version
```

### Options

- `--clean-history` — One-time: force-push a fresh orphan to the release remote, erasing all prior history. Only affects release remote; origin is untouched.

### What the Script Does (No Need to Read It)

1. **Fetches** both remotes
2. **Merges** `development` → `main` (fast-forward or merge commit)
3. **Bumps** `VERSION` file on `main`, commits
4. **Pushes** `main` to `origin` (full history, all files including `.squad/`)
5. **Builds clean tree** for `release` remote — strips forbidden paths (`.squad/`, `.ai-team/`, `devnotes/`, `docs/proposals/`, etc.)
6. **Tags** `v{X.Y.Z}` on both remotes
7. **Back-merges** VERSION bump to `development` and pushes

### Timing

- Typical duration: **30-60 seconds** (depends on push size)
- Set initial_wait to **60 seconds**

### Post-Release State

- Branch: back on `development`
- VERSION file: updated to new version
- Tags: `v{X.Y.Z}` on both `origin` and `release`
- Working tree: clean

## Verification

After the script completes, verify:

```bash
cat VERSION                          # Should show new version
git --no-pager log --oneline -3      # Should show VERSION bump commit
git --no-pager tag -l 'v*' | tail -3 # Should include new tag
git branch --show-current            # Should be 'development'
git status --short                   # Should be empty
```

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "Working tree is dirty" | Commit or stash changes first |
| "Remote 'release' not found" | `git remote add release https://github.com/OlyForge3D/PrintFarmer.git` |
| Push to release fails | Check GitHub auth: `gh auth status` |
| Merge conflict on main | Resolve manually, then re-run |
| Wrong branch | `git checkout development` first |

## Anti-Patterns

- **NEVER** read `scripts/release.sh` before running — this skill has everything you need
- **NEVER** manually edit VERSION — the script handles it
- **NEVER** manually merge development → main — the script handles it
- **NEVER** manually push tags — the script handles it
- **NEVER** run from `src/` — must be repo root
