# Squad Team

> PrintFarmer

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name            | Role                            | Charter                               | Status |
| --------------- | ------------------------------- | ------------------------------------- | ------ |
| 🏗️ Dallas        | Lead                            | .squad/agents/dallas/charter.md       | Active |
| ⚛️ Ripley        | Frontend Dev                    | .squad/agents/ripley/charter.md       | Active |
| 🔧 Lambert      | Backend Dev                     | .squad/agents/lambert/charter.md      | Active |
| 📱 Hudson       | iOS Developer                   | .squad/agents/hudson/charter.md       | Active |
| 🌐 Gorman       | iOS Networking                  | .squad/agents/gorman/charter.md       | Active |
| 🧪 Kane         | Tester                          | .squad/agents/kane/charter.md         | Active |
| 📝 Ash          | Documentation Specialist        | .squad/agents/ash/charter.md          | Active |
| 🔍 Brett        | Researcher                      | .squad/agents/brett/charter.md        | Active |
| ⚙️ Parker        | DevOps & Deployment Engineer    | .squad/agents/parker/charter.md       | Active |
| 🎨 Newt         | Designer (Industrial UI)        | .squad/agents/newt/charter.md         | Active |
| 🔍 Bishop       | Code Reviewer (Claude Opus 5)   | .squad/agents/bishop/charter.md       | Active |
| 🔍 Hicks        | Code Reviewer (GPT-5.6 Sol)     | .squad/agents/hicks/charter.md        | Active |
| 🔍 Vasquez      | Code Reviewer (Gemini 3.8 Flash, user-authorized) | .squad/agents/vasquez/charter.md | Active |
| ⚛️  Drake        | Frontend Dev                    | .squad/agents/drake/charter.md        | Active |
| 📋 Scribe       | Session Logger                  | .squad/agents/scribe/charter.md       | Active |
| 🔄 Ralph        | Work Monitor                    | —                                     | Active |
| 🛡️  Rai          | RAI Reviewer                    | .squad/agents/rai/charter.md          | Active |
| 🔍 Fact Checker | Fact Checker / Devil's Advocate | .squad/agents/fact-checker/charter.md | Active |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name     | Role         | Charter | Status          |
| -------- | ------------ | ------- | --------------- |
| @copilot | Coding Agent | —       | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**

- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**

- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**

- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer platform — printer farm management, iOS companion app, and Spoolman fork
- **Stack:** C# .NET 10, ASP.NET Core, EF Core, SignalR, React 19, TypeScript, Tailwind CSS, Vitest, xUnit, Swift 6, SwiftUI, Combine, XCTest
- **Team Root:** `.squad/` — local to this repo.
- **State Backend:** `local`
- **Created:** 2026-03-05

## Repos

| Repository         | GitHub Repo                     | Primary Language                   | Domain                                                                                  |
| ------------------ | ------------------------------- | ---------------------------------- | --------------------------------------------------------------------------------------- |
| PrintFarmer        | `OlyForge3D/PrintFarmer`        | C# .NET + React TypeScript + Swift | Backend API, React dashboard, slicer workers, iOS companion app (`mobile/`)             |
| Spoolman           | `OlyForge3D/Spoolman`           | Python + React TypeScript          | Filament spool tracking service                                                         |

## Active Issues

Tracked in `OlyForge3D/PrintFarmer`:

## Issue Source

- **Repo:** `OlyForge3D/PrintFarmer`
