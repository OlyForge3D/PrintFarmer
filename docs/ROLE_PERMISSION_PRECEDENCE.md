# Role Permission Grant/Deny Precedence

## Summary

`RolePermission.Granted` is a boolean flag on each role/resource/action assignment: `true` grants
the permission, `false` explicitly denies it. When a user holds multiple active roles, the same
(resource, action) pair may be granted by one role and denied by another.

**Rule: explicit deny wins.** If any of the user's active roles denies a permission, that
permission is excluded from the user's effective permission set, even if another active role
grants it.

This decision is tracked in [issue #1450](https://github.com/OlyForge3D/PrintFarmer/issues/1450).

## Rationale

- Deny rows exist to carve out an exception — "this role should not have X, even though some
  other role grants it." If grant always won over deny, a deny row would be unenforceable the
  moment a user has more than one role, making the `Granted = false` flag effectively dead code.
- This mirrors the common RBAC/ABAC convention of "most restrictive wins" (e.g., an explicit
  Deny in AWS IAM always overrides an Allow from another policy).
- It fails closed: conflicting role configuration results in *less* access, not more, which is
  the safer default for a permission system.

## Evaluation point

The precedence rule is applied whenever the user's effective permission set is computed — i.e.,
at **token-issue time**: on login and token refresh, inside
`AuthenticationService`/`EfUsersRepository.GetGrantedPermissionsAsync`. The resolved set is then
embedded in the issued JWT for the token's lifetime; it is not re-evaluated on every request.
This matches existing token-issuance behavior for permissions in this codebase.

## Implementation

`EfUsersRepository.GetGrantedPermissionsAsync` (`src/infra/Repositories/Users/EfUsersRepository.cs`)
computes the effective permission set as "grants minus denies" per (resource, action) pair,
across all of the user's active, non-expired roles:

1. Collect all `RolePermission` rows (both grants and denies) for the user's active roles.
2. Group the rows by (resource, action).
3. Include a (resource, action) pair in the result only if **no** row in that group has
   `Granted == false`.

### Example

| Role   | Resource | Action | Granted |
|--------|----------|--------|---------|
| A      | printers | write  | true    |
| B      | printers | write  | false   |
| A      | printers | read   | true    |

A user with roles A and B ends up with `printers:read` only. `printers:write` is denied because
role B's explicit deny overrides role A's grant.

## Test coverage

See `src/tests/Farm.Web.Api.Tests/Repositories/Users/EfUsersRepositoryPermissionPrecedenceTests.cs`
for scenarios covering: grant-only, deny-only, grant+deny conflict on the same permission,
multi-role inheritance where one role grants and another denies, deduplication across roles that
grant the same pair, denies from inactive/expired role assignments (which must not count), and a
user with no roles.

## Interaction with the `resource:admin` implication

[Issue #1447](https://github.com/OlyForge3D/PrintFarmer/issues/1447) added an implication where a
`{resource}:admin` claim satisfies every finer-grained action check on that same resource (see
`PrintFarmerPermissions.ImpliesViaResourceAdmin`). Without further changes, that implication could
silently defeat an explicit deny: if role A grants `printers:admin` and role B denies
`printers:delete`, the deny-wins rule above correctly excludes the `printers:delete` *grant*
claim, but the JWT would still carry `printers:admin`, and the admin implication would authorize
`printers:delete` anyway.

To close this gap, `AuthenticationService.GenerateJwtTokenAsync` also embeds a **deny claim**
(`PrintFarmerPermissions.DenyClaimType`, `"permission-deny"`) for every (resource, action) pair
returned by `EfUsersRepository.GetDeniedPermissionsAsync` (a mirror of
`GetGrantedPermissionsAsync` that returns any pair with at least one deny row across the user's
active roles). `ImpliesViaResourceAdmin` requires the *absence* of a matching deny claim before
implying access, so an explicit per-action deny always wins over a resource-level admin grant,
consistent with the "deny wins" rule.

The `farm_admin` role bypass (`PrintFarmerPermissions.IsFarmAdmin`) is a separate, coarser,
role-name-based "break glass" mechanism that predates `RolePermission` rows entirely and is
intentionally left unaffected by this change.

See `src/tests/Farm.Web.Api.Tests/Security/PermissionAuthorizationHandlerTests.cs` for coverage
proving a deny claim suppresses the admin implication for the denied action only (not other
actions on the same resource, and not other resources).
