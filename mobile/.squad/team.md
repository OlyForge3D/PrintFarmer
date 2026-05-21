# Squad Team

> PFarm-Ios — Native iOS client for Printfarmer 3D printer farm management

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Dallas | Lead | .squad/agents/dallas/charter.md | 🏗️ Lead |
| Ripley | iOS Dev | .squad/agents/ripley/charter.md | 📱 Active |
| Lambert | Networking | .squad/agents/lambert/charter.md | 🔧 Active |
| Ash | Tester | .squad/agents/ash/charter.md | 🧪 Active |
| Parker | UI/UX Designer | .squad/agents/parker/charter.md | 🎨 Active |
| Scribe | Session Logger | .squad/agents/scribe/charter.md | 📋 Silent |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Project Context

- **Project:** PFarm-Ios
- **User:** Jeff Papiez
- **Created:** 2026-03-06
- **Stack:** Swift, SwiftUI, iOS 17+, Xcode
- **Backend:** Printfarmer (ASP.NET Core 9.0, REST API, SignalR, JWT)
- **Backend Source:** ~/s/PFarm1
- **Description:** Native iOS app for managing 3D printer farms. Consumes Printfarmer REST API (42+ endpoints), connects via SignalR for real-time printer status, uses JWT for authentication.
