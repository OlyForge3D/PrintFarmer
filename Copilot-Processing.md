# Copilot Processing

## Request

Resume issue #1405 after backend issue #1407 merged. Integrate the canonical
`serverId` registration response and APNs `originServerId` payload field into
the existing iOS notification deep-link implementation, preserve multi-server
isolation, add focused tests, validate from `mobile/`, push the branch, and
complete the fresh pre-PR review and PR lifecycle.

## Plan

- [x] Add persisted backend origin identity to registered mobile servers.
- [x] Decode and persist the `serverId` returned by device-token registration.
- [x] Validate notification origins against the selected server while preserving
      explicit legacy payload decoding.
- [x] Add parser, router, notification-response, registration, and isolation tests.
- [x] Run focused mobile validation and fix failures caused by this change.
- [ ] Commit and push all intended changes.
- [ ] Obtain fresh Bishop/Hicks/Vasquez review at the resulting head.
- [ ] Open and own the PR for #1405 with issue linkage.

## Validation

- `xcodebuild test` focused on `DeepLinkHandlerTests`, `AppRouterTests`, and
  `PushDegradationTests`: passed on iPhone 17 simulator.
- `git diff --check`: passed.
