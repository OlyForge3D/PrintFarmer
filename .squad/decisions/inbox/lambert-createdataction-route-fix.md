# Decision: CreatedAtAction Must Use String Literals Without Async Suffix

**Author:** Lambert (Backend Dev)
**Date:** 2025-07-17
**Status:** Proposed

## Context

ASP.NET Core's `SuppressAsyncSuffixInActionNames` defaults to `true`, which strips the `Async` suffix from action names during route registration. For example, `GetByIdAsync` is registered as `GetById`.

Using `nameof(GetByIdAsync)` in `CreatedAtAction` produces the string `"GetByIdAsync"`, which does **not** match the registered route name `"GetById"`. This causes an `InvalidOperationException: No route matches the supplied values` at runtime.

## Decision

All `CreatedAtAction` calls **must** use string literals matching the registered action name (without the `Async` suffix), not `nameof()`.

```csharp
// ✅ Correct
return CreatedAtAction("GetById", new { id = entity.Id }, dto);

// ❌ Wrong — runtime exception
return CreatedAtAction(nameof(GetByIdAsync), new { id = entity.Id }, dto);
```

## Affected Controllers

- `TasksController.cs` — `nameof(GetByIdAsync)` → `"GetById"`
- `ObicoServerController.cs` — `nameof(GetServerAsync)` → `"GetServer"`

## Rationale

- `nameof()` is compile-time safe but produces the **method name**, not the **route-registered action name**.
- The ASP.NET Core convention strips `Async` from action names by default.
- String literals are the only reliable way to reference the registered action name in `CreatedAtAction`.
- This is a runtime-only failure (no compile error), making it easy to miss without integration tests.
