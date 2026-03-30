---
name: "autoprint-attention-details"
description: "Expose modal-ready reason and operator action text from auto-print status without breaking existing summary copy."
domain: "api-design"
confidence: "high"
source: "earned"
---

## Context
Use this when a compact UI surface needs richer attention details than a badge or banner can safely display, but existing clients may already depend on a single summary message.

## Patterns
- Preserve the existing summary field for backward compatibility.
- Add separate `reason` and `operatorAction` fields for modal/detail views instead of forcing the frontend to parse prose.
- Build all operator-facing copy in one backend helper so REST and SignalR stay aligned.
- Cover at least the active gate state and one non-gate blocking state (for example maintenance/unavailable) in focused backend tests.
- Mirror the contract in `src/Web/ReactApp/src/types/api.ts` when TypeScript consumes the payload.

## Examples
- Backend source: `src/infra/Services/AutoPrint/AutoPrintService.cs`
- Focused tests: `src/tests/Farm.Web.Api.Tests/Services/AutoPrint/AutoPrintServiceTests.cs`
- API integration coverage: `src/tests/Farm.Web.Api.Tests/Controllers/AutoPrintPendingReadyTests.cs`
- Frontend contract mirror: `src/Web/ReactApp/src/types/api.ts`

## Anti-Patterns
- Replacing a shipped summary field when a compatible additive change will do.
- Making the frontend split a combined sentence into separate modal sections.
- Duplicating state-specific copy in controllers, services, and frontend helpers.
