# Deployment Testing Checklist for Code Changes

**For Copilot and Contributors**: Use this checklist when modifying deployment-related files.

## Before Modifying These Files ⚠️

Always run tests AFTER making changes to:
- `scripts/docker/compose-generator.sh`
- `scripts/deploy-docker.sh`
- `scripts/docker/compose-templates/*.yml`
- `scripts/docker/configs/*.sh`

## Quick Test Command

```bash
./tests/run-deployment-tests.sh
```

**Expected Result**: ✅ ALL TESTS PASSED - Ready to commit!

## Step-by-Step Workflow

### 1️⃣ Make Your Changes

Edit the deployment script:
```bash
vim scripts/docker/compose-generator.sh
```

### 2️⃣ Run Quick Sanity Check (30 seconds)

For simple changes:
```bash
./tests/run-deployment-tests.sh --quick
```

### 3️⃣ Run Full Test Suite (3-5 minutes)

Before committing:
```bash
./tests/run-deployment-tests.sh
```

### 4️⃣ Check Results

**If All Pass** ✅:
```
✓ ALL TESTS PASSED - Ready to commit!
```
→ Safe to commit and push!

**If Tests Fail** ❌:
```
✗ SOME TESTS FAILED - Fix issues before committing
  • Failed Tests:
    - test-compose-generator.sh
```
→ Review error, fix code, run tests again

### 5️⃣ Commit When Tests Pass

```bash
git add .
git commit -m "Your change description"
```

## What Gets Tested

| Component | Tested | Coverage |
|-----------|--------|----------|
| **Architectures** | Monolithic, Microservices | 2 architectures × 3 providers = 6 combos |
| **Databases** | PostgreSQL, SQL Server, MySQL | All 3 providers |
| **Addons** | Monitoring, Telemetry, Security, Registry | All combinations |
| **Output** | YAML validity, no duplicates, permissions | Full validation |
| **Error Handling** | Invalid inputs, missing files | Graceful failures |
| **User Scenarios** | microservices + sqlserver + orcaslicer + spoolman | Exact user config |

## Common Test Results

### ✅ Success
```
════════════════════════════════════════════════════════════
✓ ALL TESTS PASSED - Ready to commit!
════════════════════════════════════════════════════════════

Test Execution Statistics:
  • Total Tests Run:    47
  • Passed:             47
  • Failed:             0
  • Skipped:            0
  • Execution Time:     4m 32s
```

### ❌ Failure (Example)
```
✗ host-network + sqlserver configuration failed
  • Docker compose validation failed
  • Error: mapping key 'volumes' already defined at line 148
```

**Fix**: Check database templates for duplicate `volumes:` keys

### ⚠️ Partial Success
```
✗ SOME TESTS FAILED - Fix issues before committing
  • Failed Tests:
    - deploy-docker.sh: architecture validation
```

**Fix**: Review the specific test failure message

## Verbose Mode (for Debugging)

If tests fail and you need more details:

```bash
./tests/run-deployment-tests.sh --verbose
```

Shows full output from each test including error messages and stack traces.

## Individual Test Files

For targeted testing when debugging:

```bash
# Test only compose generation
bash tests/test-compose-generator.sh

# Test only deploy-docker
bash tests/test-deploy-docker.sh

# Test user's exact scenario
bash tests/test-user-scenario-complete.sh

# Test configurations
bash tests/test-config-persistence.sh

# Test integration
bash tests/test-integration.sh
```

## What NOT to Do ❌

- ❌ Don't commit without running tests
- ❌ Don't ignore test failures
- ❌ Don't run tests on old code versions
- ❌ Don't modify test files to "fix" failures

## What TO Do ✅

- ✅ Run tests after every change
- ✅ Fix code issues that tests catch
- ✅ Run tests again after fixing
- ✅ Commit only when all tests pass
- ✅ Use `--verbose` if you're confused

## Test Execution Times

| Mode | Time | When to Use |
|------|------|------------|
| `--quick` | 30-60s | Before pushing to WIP branch |
| Full | 3-5min | Before final commit/merge |
| `--verbose` | 5-10min | Debugging failures |

## Exit Codes

```bash
./tests/run-deployment-tests.sh
echo $?
```

- `0` = ✅ All tests passed
- `1` = ❌ Some tests failed (fix and retry)
- `2` = ⚠️ Invalid arguments or setup error

## Environment Variables

Override test behavior with environment variables:

```bash
# Run in verbose mode
VERBOSE=true ./tests/run-deployment-tests.sh

# Run quick mode
QUICK_MODE=true ./tests/run-deployment-tests.sh
```

## Pre-Commit Checklist

Before running `git commit`:

- [ ] Made changes to deployment scripts
- [ ] Ran `./tests/run-deployment-tests.sh`
- [ ] All tests show ✅ PASSED
- [ ] No test results show ❌ FAILED
- [ ] Ready to commit

## Copilot Integration Note

**When Copilot modifies deployment scripts**:
1. After generating changes, ALWAYS run `./tests/run-deployment-tests.sh`
2. Do NOT stage changes for commit until tests pass
3. If tests fail, analyze error, fix code, run tests again
4. Provide test results summary to user

**Automatic validation commands**:
- Quick check: `./tests/run-deployment-tests.sh --quick`
- Full validation: `./tests/run-deployment-tests.sh`
- Verbose debugging: `./tests/run-deployment-tests.sh --verbose`

## Related Documentation

- Full details: `docs/DEPLOYMENT_TESTING.md`
- Architecture: `docs/DEPLOYMENT_OVERVIEW.md`
- User scenario: `docs/DEPLOY_HOST_NETWORK_SQLSERVER.md`
- Test scripts: `tests/test-*.sh`

---

**TL;DR**: After editing deployment scripts → Run `./tests/run-deployment-tests.sh` → All tests pass? → Commit! 🚀
