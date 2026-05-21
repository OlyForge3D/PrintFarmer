# SKILL: GitHub Actions iOS Code Signing Setup

**Category:** CI/CD Infrastructure  
**Platform:** GitHub Actions (macOS runners)  
**Author:** Lambert  
**Created:** 2026-03-09

## Overview

This skill provides the standard pattern for iOS code signing on GitHub Actions macOS runners using fastlane match and temporary keychains. Solves the common problem of xcodebuild hanging or failing due to keychain access issues on CI.

## When to Use

Apply this pattern when:
- ✅ Building iOS apps on GitHub Actions macOS runners
- ✅ Using `fastlane match` for certificate management
- ✅ Using `xcodebuild` for archiving/building
- ✅ Experiencing keychain access prompts or hangs on CI
- ✅ Need to ensure codesign can access certificates without UI prompts

Do NOT use if:
- ❌ Using Xcode Cloud or App Store Connect API for code signing
- ❌ Building locally (macOS default keychain works fine)
- ❌ Using different CI platforms (CircleCI, Bitrise, etc. have their own patterns)

## The Pattern

### Step 1: Setup Temporary Keychain (BEFORE match)

```yaml
- name: Setup Keychain
  env:
    KEYCHAIN_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
  run: |
    KEYCHAIN_PATH=$RUNNER_TEMP/app-signing.keychain-db
    echo "Setting up keychain at: $KEYCHAIN_PATH"
    
    # Create temporary keychain
    security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
    
    # Set keychain settings (timeout 6 hours, no lock on sleep)
    security set-keychain-settings -lut 21600 "$KEYCHAIN_PATH"
    
    # Unlock keychain
    security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
    
    # Set as default keychain
    security default-keychain -s "$KEYCHAIN_PATH"
    
    # Add to keychain search list
    security list-keychains -d user -s "$KEYCHAIN_PATH" $(security list-keychains -d user | sed 's/"//g')
    
    # Allow codesign to access keychain without prompt
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN_PATH"
    
    echo "✅ Keychain setup complete"
```

### Step 2: Configure fastlane match to Use Temporary Keychain

```yaml
- name: Setup code signing with fastlane match
  env:
    FASTLANE_USER: ${{ secrets.FASTLANE_USER }}
    FASTLANE_PASSWORD: ${{ secrets.FASTLANE_PASSWORD }}
    MATCH_PASSWORD: ${{ secrets.MATCH_PASSWORD }}
    MATCH_GIT_URL: ${{ secrets.MATCH_GIT_URL }}
    # ... other match env vars
  run: |
    KEYCHAIN_PATH=$RUNNER_TEMP/app-signing.keychain-db
    fastlane match appstore \
      --readonly \
      --git_url "$MATCH_GIT_URL" \
      --app_identifier "com.example.app" \
      --keychain_name "$KEYCHAIN_PATH" \
      --keychain_password "$MATCH_PASSWORD"
```

### Step 3: Configure xcodebuild to Use Temporary Keychain

```yaml
- name: Build for App Store
  timeout-minutes: 30
  run: |
    KEYCHAIN_PATH=$RUNNER_TEMP/app-signing.keychain-db
    xcodebuild \
      -project YourProject.xcodeproj \
      -scheme YourScheme \
      -configuration Release \
      -archivePath "build/App.xcarchive" \
      CODE_SIGN_STYLE=Manual \
      CODE_SIGN_IDENTITY="iPhone Distribution" \
      PROVISIONING_PROFILE_SPECIFIER="match AppStore com.example.app" \
      DEVELOPMENT_TEAM=TEAMID123 \
      OTHER_CODE_SIGN_FLAGS="--keychain $KEYCHAIN_PATH" \
      archive
```

### Step 4: Cleanup Keychain (ALWAYS runs)

```yaml
- name: Cleanup keychain
  if: always()
  run: |
    KEYCHAIN_PATH=$RUNNER_TEMP/app-signing.keychain-db
    security delete-keychain "$KEYCHAIN_PATH" || true
```

## Key Technical Details

### Environment Variables
- `RUNNER_TEMP` — GitHub Actions provides this; points to temporary directory for the job
- `MATCH_PASSWORD` — Secret used for both match git repo and keychain password (can reuse)

### Security Command Flags
- `create-keychain -p <password> <path>` — Create new keychain with password
- `set-keychain-settings -lut <seconds> <path>` — Lock timeout, unlock flag
  - `-l` — Lock keychain after timeout
  - `-u` — Don't lock on sleep
  - `-t <seconds>` — Timeout in seconds (21600 = 6 hours)
- `unlock-keychain -p <password> <path>` — Unlock keychain
- `default-keychain -s <path>` — Set default keychain
- `list-keychains -d user -s <paths>...` — Set keychain search list
- `set-key-partition-list -S <apps> -s -k <password> <path>` — Allow apps to access without prompt
  - `-S apple-tool:,apple:,codesign:` — Allow Apple tools and codesign
  - `-s` — Update partition list settings
  - `-k <password>` — Keychain password

### xcodebuild Flags
- `OTHER_CODE_SIGN_FLAGS="--keychain <path>"` — Tell codesign where to find signing identity
- `timeout-minutes: 30` — GitHub Actions workflow timeout (prevents infinite hangs)

## Common Issues & Solutions

### Issue: xcodebuild still hangs
**Solution:** Verify all 3 components:
1. Keychain is created and unlocked
2. Match imports to correct keychain (`--keychain_name`)
3. xcodebuild references keychain (`OTHER_CODE_SIGN_FLAGS`)

### Issue: "User interaction is not allowed"
**Solution:** Ensure `set-key-partition-list` step ran successfully. This prevents prompts.

### Issue: Multiple keychains accumulating
**Solution:** Ensure cleanup step has `if: always()` to run even on failure.

### Issue: Keychain timeout during long builds
**Solution:** Increase timeout in `set-keychain-settings` (default 21600 = 6 hours).

## Testing Checklist

- [ ] YAML syntax is valid (`python3 -c "import yaml; yaml.safe_load(open('workflow.yml'))"`)
- [ ] All secrets are configured (MATCH_PASSWORD, MATCH_GIT_URL, etc.)
- [ ] App identifier matches match configuration
- [ ] Team ID is correct
- [ ] Provisioning profile specifier matches match naming convention
- [ ] Cleanup step runs even on failure (`if: always()`)

## References

- [GitHub Docs: Installing Apple certificate on macOS runners](https://docs.github.com/en/actions/deployment/deploying-xcode-applications/installing-an-apple-certificate-on-macos-runners-for-xcode-development)
- [fastlane match documentation](https://docs.fastlane.tools/actions/match/)
- [Apple security command man page](https://ss64.com/osx/security.html)

## Real-World Usage

This pattern was validated in:
- **Project:** PrintFarmer iOS (PFarm-Ios)
- **Workflow:** `.github/workflows/testflight-beta.yml`
- **Problem Solved:** 1.5+ hour hangs at xcodebuild archive step
- **Result:** Expected build time ~10-15 minutes

## Tags

`github-actions`, `ios`, `xcode`, `code-signing`, `keychain`, `fastlane`, `match`, `ci-cd`, `macos`
