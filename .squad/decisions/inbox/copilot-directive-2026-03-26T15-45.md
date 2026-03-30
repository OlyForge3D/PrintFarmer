### 2026-03-26T15:45Z: XML documentation requirements for public C# types
**By:** Jeff Papiez (via Copilot)
**What:** When adding or updating public types, XML comments must be added/updated. All parameters for public functions must be documented in XML comments. Classes that implement interfaces should use `<inheritdoc/>` instead of duplicating documentation defined on the interface.
**Why:** User directive — enforces consistent API documentation across the codebase. Prevents doc duplication drift between interfaces and implementations.
