# Quick Test Implementation Guide

## How to Add a New Test (TDD-First Approach)

### Step 1: Write the Test (Red Phase)

Add to `/tests/test-compose-generator.sh` or `/tests/test-deploy-docker.sh`:

```bash
# Example: Testing password masking
test_password_not_logged_to_stdout() {
    start_test "password not logged to stdout"
    
    cd "$TEST_TEMP_DIR"
    
    # Run deployment script and capture output
    capture_output "$DEPLOY_SCRIPT --dry-run --batch --architecture microservices 2>&1 || true"
    local output=$(get_output)
    
    # Generate env file with password
    $DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file ./.deploy-config >/dev/null 2>&1 || true
    local env_content=$(cat .env.microservices 2>/dev/null || echo "")
    
    # Get the actual password from env
    local actual_password=$(echo "$env_content" | grep "POSTGRES_PASSWORD=" | cut -d= -f2 || echo "")
    
    # Verify password is NOT in stdout (should be masked with ***)
    if [ -n "$actual_password" ]; then
        # The plain password should NOT appear in captured output
        assert_not_contains "$output" "$actual_password" "Password should not be logged to stdout"
        
        # But masked version should appear
        assert_contains "$output" "***" "Output should show masked password indicator"
    fi
    
    pass_test
}
```

### Step 2: Verify Test Fails (Red)

```bash
cd /Users/jpapiez/s/PFarm1

# Run just the new test
bash tests/test-compose-generator.sh 2>&1 | grep "password_not_logged"
# Should see: [FAIL] ✗ password not logged to stdout
```

### Step 3: Implement Feature (Green Phase)

Edit the script (`compose-generator.sh` or `deploy-docker.sh`) to implement the feature.

### Step 4: Verify Test Passes (Green)

```bash
bash tests/test-compose-generator.sh 2>&1 | grep "password_not_logged"
# Should see: [PASS] ✓ password not logged to stdout
```

### Step 5: Verify All Tests Still Pass (Regression Check)

```bash
bash tests/test-compose-generator.sh 2>&1 | tail -3
# Should see: [PASS] All tests passed! (20/20) or higher

bash tests/test-deploy-docker.sh 2>&1 | tail -3
# Should see: [PASS] All tests passed! (24/24) or higher
```

---

## Test Framework Helpers

### Common Assertions

```bash
# File operations
assert_file_exists "$file" "Error message"
assert_file_not_exists "$file" "Error message"

# String operations
assert_contains "$string" "substring" "Error message"
assert_not_contains "$string" "substring" "Error message"
assert_equals "$expected" "$actual" "Error message"
assert_not_equals "$expected" "$actual" "Error message"

# Command execution
assert_command_success "command to run" "Error message"
assert_exit_code 1 "command to run"
assert_exit_code 0 "command to run"

# Output capture
capture_output "command to run"
local output=$(get_output)
```

### Test Lifecycle

```bash
test_my_new_feature() {
    start_test "my feature description"
    
    # Setup
    cd "$TEST_TEMP_DIR"
    
    # Run test
    # Assert results
    
    # Cleanup happens automatically in teardown
    
    pass_test  # or fail_test "error message"
}
```

---

## Quick Reference: What to Test

### For Compose-Generator Features

**Always test:**
- Happy path: Feature works as intended
- Error path: Invalid input shows clear error
- Integration: Works with other features
- YAML validity: Output passes `docker compose config --quiet`
- No duplicates: Volumes, services, networks aren't duplicated
- Consistency: Behavior is same across architectures where applicable

**Example:**
```bash
test_new_addon_stack() {
    start_test "new addon stack inclusion"
    
    # Happy path
    assert_command_success "$COMPOSE_GENERATOR --architecture microservices --include-new-addon --output-dir $TEST_TEMP_DIR"
    
    # Check files created
    assert_file_exists "$TEST_TEMP_DIR/docker-compose.yml"
    
    # Check content
    local compose=$(cat "$TEST_TEMP_DIR/docker-compose.yml")
    assert_contains "$compose" "new-addon-service" "Should include new addon service"
    assert_contains "$compose" "new-addon-network" "Should include new addon network"
    
    # Verify YAML is valid
    assert_command_success "docker compose --file $TEST_TEMP_DIR/docker-compose.yml config --quiet"
    
    # Error path: invalid combination
    assert_exit_code 1 "$COMPOSE_GENERATOR --architecture invalid --include-new-addon --output-dir $TEST_TEMP_DIR"
    
    pass_test
}
```

### For Deploy-Docker Features

**Always test:**
- Configuration generation: .deploy-config created correctly
- Environment variables: .env file has correct values
- Credentials: Passwords generated, masked properly, not logged
- Compose generation: Calls compose-generator with correct args
- Validation: Configuration validated before deployment
- Error cases: Invalid input handled gracefully

**Example:**
```bash
test_new_configuration_option() {
    start_test "new configuration option"
    
    cd "$TEST_TEMP_DIR"
    
    # Test with new option
    cat > .deploy-config << EOF
ARCHITECTURE=microservices
NEW_OPTION=true
EOF
    
    capture_output "$DEPLOY_SCRIPT --dry-run --batch --architecture microservices --config-file ./.deploy-config"
    local output=$(get_output)
    
    # Verify new option processed
    assert_contains "$output" "new option" "Should show option processing"
    
    # Verify env file includes it
    assert_file_exists ".env.microservices"
    local env=$(cat .env.microservices)
    assert_contains "$env" "NEW_OPTION=true" "Should set environment variable"
    
    pass_test
}
```

---

## Running Tests During Development

### Run specific test file
```bash
bash tests/test-compose-generator.sh
bash tests/test-deploy-docker.sh
```

### Run specific test within file
```bash
# Won't work - tests run all tests in order
# Instead, edit test file and remove/comment tests except the one you want
```

### Run with verbose output
```bash
# Edit test file, change TESTING=1 to VERBOSE=1 at top
bash tests/test-compose-generator.sh
```

### Run with bash debugging
```bash
bash -x tests/test-compose-generator.sh 2>&1 | grep "my_test" | head -50
```

---

## Common Test Pitfalls to Avoid

### ❌ Don't: Hardcode absolute paths
```bash
# BAD
assert_file_exists "/tmp/printfarmer-test-abc123/docker-compose.yml"

# GOOD
assert_file_exists "$TEST_TEMP_DIR/docker-compose.yml"
```

### ❌ Don't: Leave temp files in cleanup
```bash
# BAD
test_example() {
    # ... test code ...
    # Forgot to clean up!
}

# GOOD
test_example() {
    # ... test code ...
    rm -f "$TEST_TEMP_DIR"/* .deploy-config .env* || true
    pass_test
}
```

### ❌ Don't: Test implementation details, test behavior
```bash
# BAD - tests internal structure
grep -c "if.*ARCHITECTURE" compose-generator.sh

# GOOD - tests observable behavior
assert_contains "$output" "microservices" "Should generate microservices config"
```

### ❌ Don't: Tests that depend on order
```bash
# BAD - test 2 depends on test 1 running first
test_setup_database() { /* setup */ }
test_use_database() { /* assumes setup ran */ }

# GOOD - each test is independent
test_database_setup() { setup_db; verify_setup; cleanup; }
test_database_use() { setup_db; verify_use; cleanup; }
```

### ❌ Don't: Ignore error cases
```bash
# BAD - only tests happy path
test_invalid_port() {
    $DEPLOY_SCRIPT --http-port 99999  # Ignored!
    pass_test
}

# GOOD - verify error handling
test_invalid_port() {
    assert_exit_code 1 "$DEPLOY_SCRIPT --http-port 99999"
    pass_test
}
```

### ❌ Don't: Tests with external dependencies
```bash
# BAD - requires Docker to be running
test_deployment() {
    $DEPLOY_SCRIPT --batch --architecture microservices
    docker ps | grep printfarmer
}

# GOOD - test script behavior without docker running
test_deployment_config() {
    $DEPLOY_SCRIPT --dry-run --batch --architecture microservices
    assert_file_exists "docker-compose.yml"
}
```

---

## Committing Test Changes

```bash
# After writing and passing new tests:
git add tests/test-compose-generator.sh tests/test-deploy-docker.sh

# Commit message format:
# TDD: Add test for [feature name]
# (tests added before implementation)
#
# Adds test_[feature]() covering:
# - Happy path: ...
# - Error cases: ...
# - Integration with: ...

git commit -m "TDD: Add test for password masking in deploy script"
```

### Before merging to main
```bash
# Run full test suite one more time
bash tests/test-compose-generator.sh
bash tests/test-deploy-docker.sh

# Check for regressions
git diff origin/main tests/

# Verify no hardcoded paths or OS assumptions
grep -r "/Users/jpapiez" tests/  # Should be empty!
grep -r "^/tmp/" tests/          # Should be empty!
```

---

## Debugging Failed Tests

### When a test fails:

1. **Check assertion message**
   ```
   [FAIL] ✗ test_name - Expected 'value', got 'other'
   ```

2. **Run test individually with debug info**
   ```bash
   # Add debugging to test file temporarily
   test_example() {
       start_test "example"
       
       # ... setup ...
       
       echo "DEBUG: Generated file contents:"
       cat "$TEST_TEMP_DIR/docker-compose.yml" >&2
       
       # ... assertions ...
   }
   ```

3. **Check temp directory**
   ```bash
   ls -la /var/folders/1y/98qd39cj0m3gfpzp50lp8q040000gn/T/printfarmer-test-*/
   ```

4. **Compare expected vs actual**
   ```bash
   # Diff generated vs template
   diff <(echo "$expected") <(echo "$actual")
   ```

5. **Test the script manually**
   ```bash
   cd /tmp/test
   /Users/jpapiez/s/PFarm1/scripts/docker/compose-generator.sh --architecture microservices --output-dir .
   cat docker-compose.yml | head -50
   ```

---

## Performance: Tests Should Be Fast

### Target: All tests complete in < 15 minutes

```
compose-generator tests: ~5-10 minutes (20 tests)
deploy-docker tests: ~8-15 minutes (24 tests)
```

**If tests are slow:**
- Use `--dry-run` to skip actual deployment
- Use `--batch` to skip interactive prompts
- Mock external services (don't actually test Docker)
- Reduce number of architecture/provider combinations in a single test

---

## Resources

- **Test Framework**: `/tests/test-framework.sh` - Assertion functions
- **Example Tests**: `/tests/test-compose-generator.sh` - Real examples
- **Compose Generator**: `/scripts/docker/compose-generator.sh` - Feature reference
- **Deploy Script**: `/scripts/deploy-docker.sh` - Feature reference
- **Test Coverage Analysis**: `/docs/TEST_COVERAGE_ANALYSIS.md` - This analysis

---

## Questions?

- What does the test framework provide? → See `test-framework.sh`
- How do I test a Docker command? → Use capture_output + grep
- How do I clean up after tests? → Use `$TEST_TEMP_DIR` - cleaned automatically
- What if my test needs to modify system? → Use temp files only, never /etc or system files
- Can I test the actual Docker deployment? → Yes, but requires Docker running (complex, slower)
