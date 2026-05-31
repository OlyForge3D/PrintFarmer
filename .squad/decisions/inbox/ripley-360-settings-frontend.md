## Decision: Settings Frontend Architecture (Issue #360)

**Date:** 2025-07-22
**Author:** Ripley (Frontend)

### Context

Implementing frontend pages for the per-user vs farm-wide settings split (backend shipped in #359/PR #385).

### Decisions

1. **Separate inner form components** — FarmSettingsForm and UserSettingsForm are separate components that receive data as props, initializing `useState` from prop values. This avoids the `useEffect` → `setState` anti-pattern flagged by the ESLint `react-hooks/set-state-in-effect` rule.

2. **Route at `/preferences`** — The new page lives at `/preferences` (no role guard). Farm settings show a lock badge + read-only fields for non-admins using the `canWrite` flag from the API. The existing admin `/settings` route (metadata-driven) remains untouched.

3. **React Query hooks** — `useFarmSettings` / `useUpdateFarmSettings` / `useUserSettings` / `useUpdateUserSettings` use the public `apiClient.get<T>` / `apiClient.put<T>` methods. Optimistic cache update on mutation success via `queryClient.setQueryData`.

4. **Client-side validation mirrors backend** — Same min/max ranges. Toast errors for invalid input before sending request.

### Alternatives Considered

- Embedding in existing SettingsPage — rejected because that page is admin-only and metadata-driven. The new endpoints have a different shape and audience.
- `react-hook-form` — charter says controlled `useState` is the convention.
