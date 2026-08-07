# Test Documentation Index

Use this index to find the current testing guidance for PrintFarmer.

## Application Tests

- [Development Guide: Testing](./DEVELOPMENT.md#testing) covers the primary
  .NET and React commands used during development.
- [Testing Patterns](./TESTING_PATTERNS.md) documents reusable backend test
  patterns, organization, coverage goals, and common pitfalls.
- [Test Documentation Index](./TEST_DOCUMENTATION_INDEX.md) is this navigation
  page.

## Deployment Script Tests

- [Testing Guidelines](./TESTING_GUIDELINES.md) covers the deployment script
  suites, test framework, TDD workflow, review checklist, and performance
  expectations.
- [Deployment Script Testing Guide](./DEPLOYMENT_TESTING.md) explains when to
  run each deployment suite and how to interpret failures.
- [Deployment Testing Checklist](./DEPLOYMENT_TESTING_CHECKLIST.md) is the
  concise pre-commit checklist for deployment tooling changes.

The deployment test scripts live under the repository's `tests/` directory:

- [`tests/test-compose-generator.sh`](../tests/test-compose-generator.sh)
- [`tests/test-deploy-docker.sh`](../tests/test-deploy-docker.sh)
- [`tests/test-framework.sh`](../tests/test-framework.sh)

## Documentation Health

Run the focused checker tests:

```bash
node --test scripts/docs/tests/check-markdown.test.mjs
```

Check all relative Markdown links and document structure in `README.md` and
`docs/**/*.md`:

```bash
node scripts/docs/check-markdown.mjs
```

The checker validates local targets and fragments, enforces path casing on
case-insensitive systems, and rejects unclosed code fences. It also rejects
Markdown heading syntax inside code fences for languages where `# Heading` is
not valid source syntax, which catches prose accidentally pasted into code
examples. External URLs and intentional non-file URI schemes are excluded.
