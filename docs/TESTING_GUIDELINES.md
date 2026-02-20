# Testing Guidelines for PrintFarmer Deployment Scripts

This document outlines the testing strategy and guidelines for `deploy-docker.sh` and `compose-generator.sh`.

## Quick Links

- **Test Coverage Analysis**: See [`/docs/TEST_COVERAGE_ANALYSIS.md`](./TEST_COVERAGE_ANALYSIS.md) for complete analysis of all 44 tests and 37 identified gaps
- **Implementation Guide**: See [`/docs/QUICK_TEST_IMPLEMENTATION_GUIDE.md`](./QUICK_TEST_IMPLEMENTATION_GUIDE.md) for step-by-step TDD workflow
- **Test Files**: 
  - [`/tests/test-compose-generator.sh`](/tests/test-compose-generator.sh) (20 tests)
  - [`/tests/test-deploy-docker.sh`](/tests/test-deploy-docker.sh) (24 tests)
  - [`/tests/test-framework.sh`](/tests/test-framework.sh) (test utilities)

---

## Current Status

✅ **All 44 tests passing**
- Compose-Generator: 20/20 tests
- Deploy-Docker: 24/24 tests
- Execution time: ~15 minutes

⚠️ **37 missing tests identified** (gaps documented in analysis)
- 12 compose-generator gaps
- 25 deploy-docker gaps

---

## Running Tests

### Run all tests
```bash
cd /Users/jpapiez/s/PFarm1

# Compose-generator tests (5-10 minutes)
bash tests/test-compose-generator.sh

# Deploy-docker tests (8-15 minutes)
bash tests/test-deploy-docker.sh

# Both together (15 minutes total)
bash tests/run-tests.sh --fast
```

### Run with different options
```bash
# Fast mode (skip slow integration tests)
bash tests/run-tests.sh --fast

# Specific test suite
bash tests/run-tests.sh --suite compose-generator
bash tests/run-tests.sh --suite deploy-docker

# Verbose output
bash tests/run-tests.sh --verbose

# Parallel execution (experimental)
bash tests/run-tests.sh --parallel
```

### Test individual functionality
```bash
# Tests must be run with full file
# Edit test file to comment out other tests temporarily, or:

bash tests/test-compose-generator.sh 2>&1 | grep "microservices"
bash tests/test-deploy-docker.sh 2>&1 | grep "password"
```

---

## Before Committing Changes

⚠️ **Critical**: Always run tests after modifying `deploy-docker.sh` or `compose-generator.sh`

```bash
# 1. Make your changes
# ... edit scripts ...

# 2. Run affected tests
bash tests/test-compose-generator.sh   # If changed compose-generator.sh
bash tests/test-deploy-docker.sh       # If changed deploy-docker.sh

# 3. Verify all tests pass
# Should see: [PASS] All tests passed! (20/20) and (24/24)

# 4. Commit only if all tests pass
git add scripts/deploy-docker.sh scripts/docker/compose-generator.sh
git commit -m "Feature: description (all tests passing)"
```

---

## TDD Workflow (Required for New Features)

### For any new feature, follow TDD discipline:

1. **Write Test First (RED)**
   ```bash
   # Add failing test to appropriate test file
   test_my_new_feature() {
       start_test "my feature description"
       # ... test code that describes desired behavior ...
       pass_test
   }
   
   # Verify test fails
   bash tests/test-compose-generator.sh 2>&1 | grep "my_new_feature"
   # Should see: [FAIL] ✗ my new feature description
   ```

2. **Implement Feature (GREEN)**
   ```bash
   # Now implement the feature in the script
   # Edit compose-generator.sh or deploy-docker.sh
   
   # Verify test passes
   bash tests/test-compose-generator.sh 2>&1 | grep "my_new_feature"
   # Should see: [PASS] ✓ my new feature description
   ```

3. **Refactor (keep tests green)**
   ```bash
   # Clean up code while ensuring tests still pass
   bash tests/test-compose-generator.sh
   # Should see: [PASS] All tests passed! (20/20)
   ```

4. **Update Documentation**
   ```bash
   # Update TEST_COVERAGE_ANALYSIS.md
   # Move test from "Missing Tests" to "Current Tests"
   # Add to this TESTING_GUIDELINES.md if needed
   ```

---

## Test Writing Guidelines

### Best Practices

✅ **DO:**
- Write tests that describe observable behavior, not implementation details
- Make tests independent (can run in any order)
- Use `$TEST_TEMP_DIR` for temporary files (auto-cleaned)
- Test both happy path and error cases
- Keep test names descriptive: `test_what_happens_when_something_occurs()`
- Include assertions with clear error messages
- Clean up any generated files

❌ **DON'T:**
- Hardcode absolute paths (use `$TEST_TEMP_DIR`, `$REPO_ROOT`)
- Create tests that depend on external services (use `--dry-run`)
- Leave test files in `/tmp` (use provided test cleanup)
- Test implementation details (test behavior)
- Make tests take > 30 seconds each (except integration tests)
- Ignore error cases
- Assume OS (use portable commands)

### Test Structure Template

```bash
test_descriptive_name() {
    start_test "what this test validates"
    
    # Setup
    cd "$TEST_TEMP_DIR"
    
    # Execute
    capture_output "command to test"
    local output=$(get_output)
    
    # Verify
    assert_contains "$output" "expected text" "Error if not found"
    assert_file_exists "expected_file"
    
    # Cleanup happens automatically
    pass_test
}
```

---

## Test Framework Reference

### Available Assertions

```bash
# File operations
assert_file_exists "$file" "error message"
assert_file_not_exists "$file" "error message"

# String operations
assert_contains "$haystack" "needle" "error message"
assert_not_contains "$haystack" "needle" "error message"
assert_equals "$expected" "$actual" "error message"
assert_not_equals "$expected" "$actual" "error message"

# Command execution
assert_command_success "command"           # Exit code 0
assert_exit_code 1 "command"               # Specific exit code

# Output capture
capture_output "command to run"
local output=$(get_output)
```

### Test Lifecycle Functions

```bash
start_test "test description"    # Begin test, increment counter
pass_test                        # Mark test as passed
fail_test "error message"        # Mark test as failed
capture_output "command"         # Run command, capture output
get_output                       # Get captured output
```

---

## Debugging Failed Tests

### When a test fails:

1. **Read the assertion message**
   - Shows expected vs actual value

2. **Inspect temp directory**
   ```bash
   # After test failure, temp dir still exists for inspection
   ls -la /var/folders/.../T/printfarmer-test-*
   ```

3. **Run test manually**
   ```bash
   # Reproduce test conditions outside test harness
   mkdir /tmp/debug-test
   cd /tmp/debug-test
   /Users/jpapiez/s/PFarm1/scripts/docker/compose-generator.sh
   cat docker-compose.yml
   ```

4. **Check script changes**
   ```bash
   # Verify your changes didn't break logic
   git diff scripts/compose-generator.sh
   ```

5. **Add debug output**
   ```bash
   # Temporarily add echo statements to test
   test_example() {
       start_test "example"
       echo "DEBUG: variable=$variable" >&2
       # ... rest of test ...
   }
   ```

---

## Performance Expectations

### Test Execution Times

| Suite | Count | Time | Notes |
|-------|-------|------|-------|
| compose-generator | 20 | 5-10 min | Yaml parsing + file operations |
| deploy-docker | 24 | 8-15 min | More file I/O + env var testing |
| **Total** | **44** | **~15 min** | Acceptable for CI/CD |

If tests are slow:
- Use `--dry-run` instead of actual deployment
- Use `--batch` to skip interactive prompts
- Reduce number of combinations tested in single test
- Consider splitting slow tests into separate suite

---

## Test Gaps (Missing Tests)

### High Priority Gaps (Prevent Critical Failures)

These should be implemented first:
1. **Password security** (logging, masking)
2. **YAML validation** (compose file structure)
3. **Database initialization** (service order, healthchecks)
4. **Data persistence** (volume mounting)
5. **Network configuration** (host-network binding)

### Medium Priority Gaps (Quality Improvements)

Implement after high-priority:
- Error handling (permissions, ports)
- Configuration validation
- Frontend setup (CORS, env files)
- Addon deployments

### Low Priority Gaps (Edge Cases)

Implement as features evolve:
- Special character handling
- Architecture support edge cases
- Logging and diagnostics

See `/docs/TEST_COVERAGE_ANALYSIS.md` for complete details on all 37 gaps.

---

## CI/CD Integration

### Pre-commit Hook Setup

Create `.git/hooks/pre-commit`:
```bash
#!/bin/bash
# Run tests before allowing commit
cd $(git rev-parse --show-toplevel)
bash tests/test-compose-generator.sh || exit 1
bash tests/test-deploy-docker.sh || exit 1
```

Make executable: `chmod +x .git/hooks/pre-commit`

### GitHub Actions / GitLab CI

Add to CI/CD configuration to run tests on every merge request:
```yaml
test_deployment_scripts:
  script:
    - timeout 300 bash tests/test-compose-generator.sh
    - timeout 900 bash tests/test-deploy-docker.sh
  timeout: 20 minutes
```

---

## Code Review Checklist

Before approving changes to deployment scripts:

- [ ] All tests pass: `bash tests/test-compose-generator.sh` and `bash tests/test-deploy-docker.sh`
- [ ] New features have corresponding tests (TDD - tests added first)
- [ ] Tests cover both success and failure cases
- [ ] No test-specific file paths hardcoded (use `$TEST_TEMP_DIR`)
- [ ] All existing tests still pass (no regressions)
- [ ] Test names clearly describe what they test
- [ ] Documentation updated (`TEST_COVERAGE_ANALYSIS.md`)
- [ ] No password/secrets exposure in tests

---

## Resources

- **Test Framework Code**: `/tests/test-framework.sh`
- **Test Analysis**: `/docs/TEST_COVERAGE_ANALYSIS.md`
- **Implementation Guide**: `/docs/QUICK_TEST_IMPLEMENTATION_GUIDE.md`
- **Compose-Generator Script**: `/scripts/docker/compose-generator.sh`
- **Deploy Script**: `/scripts/deploy-docker.sh`

---

## Questions?

See the detailed guides:
- [TEST_COVERAGE_ANALYSIS.md](./TEST_COVERAGE_ANALYSIS.md) - Why each test matters
- [QUICK_TEST_IMPLEMENTATION_GUIDE.md](./QUICK_TEST_IMPLEMENTATION_GUIDE.md) - How to write tests

Or check the existing test files for examples of working tests.
