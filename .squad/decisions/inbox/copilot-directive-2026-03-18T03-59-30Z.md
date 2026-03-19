### 2026-03-18T03:59:30Z: User directive — Obico integration design
**By:** Jeff Papiez (via Copilot)

**What:**
1. **Printer opts-in, app decides server.** Users enable Obico monitoring on a printer (simple toggle), but the APP chooses which Obico server handles that printer — not the user. Remove the Obico server dropdown from the printer edit form.
2. **Camera required.** When enabling Obico on a printer, the app must verify the printer has a camera configured. If no camera, block enable and show an error explaining why.
3. **Server validation on add.** When adding/enabling an Obico server in settings, the backend must validate the server is healthy AND all required APIs are accessible (not just `/p/` — verify all endpoints needed for snapshot submission and spaghetti detection). Reject the add/enable if validation fails.

**Why:** User request — simplifies UX (users shouldn't pick servers), enforces prerequisites (camera), prevents misconfiguration (server health).
