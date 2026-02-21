---
applyTo: '**'
description: 'PrintFarmer workspace directory conventions: correct working directories for git, build, and test commands'
---

# Workspace Directory Conventions

## Repository Layout & Working Directories

The **repo root** is the directory containing `.git/`, `src/`, `scripts/`, and `Dockerfile.multistage`. It is the first workspace folder shown in `<workspace_info>`. Many commands require specific working directories. Getting this wrong causes silent failures, wrong file paths in `git add`, or "file not found" build errors.

```
<repo-root>/                      ← REPO ROOT (git commands, deploy scripts, docker compose)
├── src/                          ← .NET SOLUTION ROOT (dotnet build/test/format)
│   ├── farm-web.sln
│   ├── api/                      ← API project
│   ├── backends/                 ← Backend plugins
│   ├── migrations/               ← EF Core migrations
│   ├── slicer/                   ← Slicer projects
│   └── Web/ReactApp/             ← REACT ROOT (npm commands)
│       ├── package.json
│       └── src/                  ← React source files
```

Resolve `<repo-root>` from the workspace info provided at conversation start. For example, if the workspace folder is `/home/user/projects/pfarm1`, then:
- Repo root = `/home/user/projects/pfarm1`
- .NET solution root = `/home/user/projects/pfarm1/src`
- React root = `/home/user/projects/pfarm1/src/Web/ReactApp`

## Command → Required Directory

| Command | Must Run From | Common Mistake |
|---|---|---|
| `git add / commit / push / status` | Repo root | Running from `src/` causes paths like `src/api/Foo.cs` to not match |
| `git diff`, `git log` | Repo root | Same — relative paths are wrong from `src/` |
| `dotnet build ./farm-web.sln` | `src/` | Running from repo root fails (no .sln there) |
| `dotnet test ./farm-web.sln` | `src/` | Same |
| `dotnet format ./farm-web.sln` | `src/` | Same |
| `dotnet ef migrations add` | `src/` | Same — project paths are relative to `src/` |
| `dotnet run --project ./api/...` | `src/` | Same |
| `npm install / run build / test` | `src/Web/ReactApp/` | Running from `src/` fails (no package.json) |
| `npm run dev` | `src/Web/ReactApp/` | Same |
| `npm run lint` | `src/Web/ReactApp/` | Same |
| `npm run test:run` | `src/Web/ReactApp/` | Same |
| `./scripts/deploy-docker.sh` | Repo root | Script expects repo root for `.env`, `Dockerfile.multistage`, etc. |
| `docker compose up/down` | Repo root | `docker-compose.yml` is at repo root |
| `bash -n scripts/deploy-docker.sh` | Repo root | Script path is relative to root |

## Rules

1. **Always `cd` to the correct directory before running a command.** Use absolute paths derived from the workspace root.

2. **For git operations, always be at the repo root.** Before any `git add`, `git commit`, `git status`, or `git diff`, `cd` to `<repo-root>`.

3. **For .NET commands, always be in `src/`.** Before any `dotnet build`, `dotnet test`, `dotnet format`, or `dotnet ef`, `cd` to `<repo-root>/src`.

4. **For React/npm commands, always be in `src/Web/ReactApp/`.** Before any `npm` command, `cd` to `<repo-root>/src/Web/ReactApp`.

5. **When chaining commands across directories, use `&&` with explicit `cd`.** Example:
   ```bash
   cd <repo-root>/src && dotnet build ./farm-web.sln && cd <repo-root> && git add -A
   ```

6. **Never assume the current directory.** The terminal may have been left in any directory from a previous command. Always set it explicitly.

## File Path Conventions in git add

When staging files, paths must be relative to the repo root (where `.git/` lives):

```bash
# CORRECT (from repo root):
git add src/api/Controllers/FooController.cs
git add .github/instructions/my-file.instructions.md
git add Dockerfile.multistage

# WRONG (from src/):
git add api/Controllers/FooController.cs          # ← git won't find this
git add src/api/Controllers/FooController.cs      # ← double-nested, wrong path
```

## Quick Reference: Paths Relative to Repo Root

- **Repo root**: `<repo-root>` (from workspace info)
- **.NET solution**: `<repo-root>/src`
- **React app**: `<repo-root>/src/Web/ReactApp`
- **Deploy script**: `<repo-root>/scripts/deploy-docker.sh`
- **Docker templates**: `<repo-root>/scripts/docker/`
- **Instructions**: `<repo-root>/.github/instructions/`
