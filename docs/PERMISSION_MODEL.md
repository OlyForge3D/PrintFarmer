# Permission Model

PrintFarmer authorization is **permission-based**, not role-based. Roles exist only as the
container that groups permissions together and assigns them to users - a role has no meaning
to the authorization pipeline beyond the set of `resource:action` permissions attached to it
(plus one hardcoded exception, the `farm_admin` bypass, described below). There is no per-user
permission override: the only way a user gains a permission is through an active role
assignment.

This page documents the model as implemented today. See also:

- [Role Permission Precedence](./ROLE_PERMISSION_PRECEDENCE.md) - the grant/deny precedence
  rule in full detail, including its interaction with the `admin` implication.
- [Issue #1445](https://github.com/OlyForge3D/PrintFarmer/issues/1445) - the epic that migrated
  the API from role-name gates to this model.

## The model

### Resources and actions

Every enforced permission is a `resource:action` pair, e.g. `printers:read` or `roles:admin`.
Resources and actions are rows in two seeded catalog tables
(`DatabaseInitializer.SeedResourcesAsync`, `SeedUserActionsAsync`,
`src/api/Services/Startup/DatabaseInitializer.cs`), and `RolePermission` is the join table that
grants or denies a specific `(Resource, Action)` pair to a specific `Role`
(`src/infra/Domain/RolePermission.cs`).

Stable, compile-time-checked permission name constants live in `PrintFarmerPermissions`
(`src/infra/Security/PrintFarmerPermissions.cs`), grouped by resource
(`PrintFarmerPermissions.Calibration.Read`, `PrintFarmerPermissions.Queue.Cancel`, etc.) so
controller code never hardcodes a permission string.

### `RequirePermission` binds an endpoint to a claim

`[RequirePermission("resource", "action")]` (or the single-string form,
`[RequirePermission("resource:action")]`) is an `AuthorizeAttribute` subclass
(`src/infra/Authorization/RequirePermissionAttribute.cs`) applied to a controller class,
controller action, or SignalR hub method. At request time,
`PermissionAuthorizationHandler.HandleRequirementAsync` checks whether the caller's
`ClaimsPrincipal` carries a `permission` claim equal to `resource:action`. That claim is
populated at token-issue time (see below), not looked up against the database per request.

### Roles are the only grant vehicle

A user's effective permissions are the union of every `RolePermission` grant across their
active, non-expired `UserRole` assignments (`UserRole.IsActive`, `UserRole.ExpiresAt`). There is
no mechanism to grant or deny a permission to an individual user directly - to change what a
user can do, you change which roles they hold, or change what a role grants.

## The `admin` implication

Granting `{resource}:admin` on a role implies every other action on that same resource. For
example, a role holding `calibration:admin` can also perform `calibration:read`,
`calibration:update`, etc., without those actions being separately granted. This is implemented
once, in `PrintFarmerPermissions.ImpliesViaResourceAdmin`, and every enforcement point (the
authorization handler, SignalR hubs, capability services) calls through that single method so
they cannot drift apart.

What it explicitly does **not** do:

- **No cross-resource reach.** `printers:admin` never implies anything about `queue:*` or any
  other resource. The implication is scoped to one resource only.
- **No action hierarchy beyond `admin` itself.** There is no `write` implies `read`, or similar
  chain - `admin` is the only action that implies others, and it implies *all* other actions on
  its resource, not a subset.

## Grant/deny precedence

`RolePermission.Granted` is a boolean: `true` grants the permission, `false` explicitly denies
it. A user can hold multiple active roles, and the same `(resource, action)` pair may be granted
by one role and denied by another.

**Explicit deny always wins.** If any of a user's active roles denies a permission, it is
excluded from that user's effective permission set even though another active role grants it.
This mirrors the common "most restrictive wins" RBAC/ABAC convention (e.g. an explicit Deny in
AWS IAM overrides an Allow from another policy) and fails closed: conflicting role configuration
results in *less* access, never more.

This also interacts with the `admin` implication above: a deny on `printers:delete` must still
win even if another of the user's roles grants `printers:admin`. To make that hold, token
issuance embeds a second claim type - `permission-deny` - for every denied `(resource, action)`
pair, and `ImpliesViaResourceAdmin` refuses to imply access when a matching deny claim is
present. See [Role Permission Precedence](./ROLE_PERMISSION_PRECEDENCE.md) for the full
mechanism, evaluation point (token-issue time, not per-request), and test coverage.

## The `farm_admin` bypass

`farm_admin` is a hardcoded role name (`PrintFarmerPermissions.FarmAdminRole`), and
`PrintFarmerPermissions.IsFarmAdmin` checks for it directly via `ClaimsPrincipal.IsInRole`. When
a caller is in the `farm_admin` role, `PermissionAuthorizationHandler` grants every
`[RequirePermission]` check unconditionally, before it even looks at the caller's `permission`
claims.

This bypass:

- **Predates `RolePermission` rows entirely.** It is a separate, coarser mechanism from the
  grant/deny system described above, and is intentionally left unaffected by the deny-precedence
  rule.
- **Is audited.** Every time the bypass is the reason a check succeeds, the handler logs an
  informational "Audited farm-admin permission bypass" entry naming the user and the permission
  that was bypassed.
- **Explains why `farm_admin` appears to have permissions it was never explicitly granted.**
  `farm_admin` is *also* seeded with an explicit `{resource}:admin` grant for every resource
  (`DatabaseInitializer.SeedRolePermissionsAsync`), so most of its access is visible in
  `RolePermission` rows too - but any permission enforced in code that was never added to that
  seed list (or to a custom role) is still reachable by `farm_admin` through the bypass alone,
  with no corresponding grant row. `PermissionGrantPathTests` exists specifically to catch that
  gap for every other role: it fails if any `[RequirePermission]` permission has no real,
  non-bypass grant path.

## System vs custom roles

Two system roles are seeded and protected: `farm_admin` and `farm_user`
(`RoleManagementService`, `RolePermissionService`). System roles (`Role.IsSystemRole`):

- Can never be renamed, deactivated, or deleted through the roles API.
- `DisplayName` and `Description` remain editable.
- `farm_admin` specifically cannot have its permissions edited at all - the permissions API
  rejects any attempt with `FarmAdminImmutable`, because its access comes from the bypass, not
  from its `RolePermission` rows.

Custom roles are created with `POST /api/admin/roles` and must have a **name that is an
immutable slug**: lowercase letters, digits, and underscores, 3-50 characters, starting with a
letter (`^[a-z][a-z0-9_]{2,49}$`), and the reserved `farm_` prefix is rejected. The name is fixed
at creation time - `UpdateRoleAsync` rejects any attempt to change it after creation - so that
other systems (audit logs, seed scripts, external references) can rely on a role's name never
moving out from under them. `DisplayName`, `Description`, permission grants, and active status
remain editable.

Deactivating or deleting a role is refused if it would leave the system with no active role
still holding both `roles:admin` and `users:admin` (the "last admin" guardrail), or if it would
strip the acting administrator of their own last administrative role (self-lockout guardrail).

## Operating guide

To create a role, grant permissions, and assign users through the admin UI or API:

1. **Create the role** - `POST /api/admin/roles`, either with an explicit `permissions` list or
   `copyFromRoleId` to clone another role's grants as a starting point.
2. **Grant or adjust permissions** - `PUT /api/admin/roles/{roleId}/permissions` with the full
   desired permission set for that role (full-replacement semantics: permissions present in the
   derived catalog but omitted from the request are removed). The request must include the
   role's current `updatedAt` value; a stale value is rejected as a concurrency conflict.
3. **Assign users to the role** - via the users administration surface (`UsersService`), which
   creates or deactivates `UserRole` rows.
4. **Saving a permission change signs out affected users.** Both changing a role's permissions
   and changing a user's role assignment revoke every active token for every affected user
   (`IEffectivePermissionsRevocationService.RevokeUsersAsync`), because permissions are resolved
   once at token-issue time and embedded in the JWT for that token's lifetime - they are not
   re-evaluated per request. Affected users must sign in again to receive a token reflecting the
   new permission set.

## Contributor guide: adding a new enforced permission

Adding a new `[RequirePermission]` check without also giving some role a way to reach it creates
exactly the gap `PermissionGrantPathTests` was written to catch (see
`src/tests/Farm.Web.Api.Tests/Security/PermissionGrantPathTests.cs`): a permission enforced in
code, reachable only through the `farm_admin` bypass, and invisible to every other role no
matter what it's granted. To pass that drift guard when adding a new permission:

1. **Add the resource (if new) to `DatabaseInitializer.SeedResourcesAsync`.** The resource must
   exist in the seeded catalog, or even `farm_admin`'s blanket `{resource}:admin` grant cannot be
   created for it.
2. **Add the action (if new) to the seeded `UserAction` catalog**, and add a stable constant for
   the full permission to `PrintFarmerPermissions` (grouped under the resource's nested static
   class) rather than hardcoding the string at the call site.
3. **Apply `[RequirePermission(resource, action)]`** to the controller, action method, or hub
   method that should enforce it. Multiple `[RequirePermission]` attributes on the same member
   are combined with AND semantics - every permission in the group must be satisfiable by a
   single role simultaneously.
4. **Give a non-`farm_admin` role a real grant path**, one of:
   - Add the `(resource, action)` pair to `farm_user`'s grants in
     `DatabaseInitializer.SeedRolePermissionsAsync` (purely additive - never remove an existing
     grant, since reseeding an existing deployment must not take permissions away); or
   - Grant a role `{resource}:admin` instead, which implies the new action via the admin
     implication; or
   - If the permission is genuinely and deliberately administrative - an unscoped, farm-wide
     action with no per-printer/per-group authorization boundary - add it to
     `PermissionGrantPathTests.AdminOnlyAllowlist` with a written reason explaining why no
     non-admin role should ever hold it. This is the explicit escape hatch; use it sparingly and
     only with a real design justification, following the existing entries (`obico:manage`,
     `queue:reconcile`, `dispatch-settings:manage`) as examples.
5. **Run the guard tests** before opening a PR: `RoleToPermissionMigrationCompletenessTests` and
   `AuthorizeRolesGateArchitectureTests` (no role-name `[Authorize(Roles = ...)]` gates),
   `PermissionSeedTests` (seeding is idempotent and additive), and
   `PermissionGrantPathTests.EveryEnforcedPermission_HasAGrantPathOrDocumentedAllowlistEntry`
   (the drift guard itself).
