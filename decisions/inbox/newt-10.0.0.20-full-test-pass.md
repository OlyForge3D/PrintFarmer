# Newt full deployed-app validation pass

- Target: http://10.0.0.20
- Date: 2026-06-17
- Browser: playwright-cli with msedge (headed=false via CLI runtime)

## Scope executed
- Unauthenticated smoke pass over landing/login and auth-adjacent routes.
- Printables and Notifications route probes (direct deep links) to assess accessibility and guard behavior.
- Basic auth UX checks: register transition, forgot-password link behavior, invalid login response.

## Coverage results
- Login page load: PASS
- Register entry from login: PASS (Create Account form opens)
- Invalid credential login handling: PASS ("Login failed" shown)
- Forgot password navigation from login: FAIL (stays on /login after clicking link)
- Printables routes (/printables, /printables/import, /printables/public, /printables/collections): AUTH-BLOCKED (redirect to /login)
- Notifications routes (/notifications, /settings/notifications): AUTH-BLOCKED (redirect to /login)

## Defects
1. Forgot password link does not navigate.
   - Severity: High
   - Repro:
     1) Open /login
     2) Click "Forgot password?"
     3) Observe URL remains /login
   - Expected: Navigate to /forgot-password (or open dedicated reset view)
   - Actual: No route change; sign-in view remains
   - Evidence: .playwright-cli/newt-08-login-recheck.yaml, .playwright-cli/newt-09-after-forgot-click.yaml, .playwright-cli/newt-09-after-forgot-click.png

2. Login page triggers unauthorized workers API call before authentication.
   - Severity: Medium
   - Repro:
     1) Open /login with no active session
     2) Observe console/network
   - Expected: No protected workers call on unauthenticated login screen
   - Actual: 401 on /api/workers emitted immediately
   - Evidence: .playwright-cli/console-2026-06-17T21-07-19-712Z.log, .playwright-cli/network-2026-06-17T21-07-49-083Z.log

## Evidence artifacts
- .playwright-cli/newt-01-login.png
- .playwright-cli/newt-02-forgot.yaml
- .playwright-cli/newt-03-forgot-submit.yaml
- .playwright-cli/newt-04-register.yaml
- .playwright-cli/newt-04-register.png
- .playwright-cli/newt-05-invalid-login.yaml
- .playwright-cli/newt-05-invalid-login.png
- .playwright-cli/newt-06-printables-guest.yaml
- .playwright-cli/newt-07-notifications-guest.yaml
- .playwright-cli/newt-09-after-forgot-click.png
- .playwright-cli/newt-10-printables-import.yaml
- .playwright-cli/newt-11-printables-public.yaml
- .playwright-cli/newt-12-printables-collections.yaml
- .playwright-cli/newt-13-settings-notifications.yaml

## Risks / gaps
- No credentials were provided; authenticated in-app flows could not be exercised.
- Could not validate Printables feature internals (browse/import/username/public collections behavior) beyond auth guards.
- Could not validate Notifications preferences matrix/channel states/push UX in authenticated settings.
