# Installing ruamel.yaml on Your VM

**Date**: November 1, 2025  
**Required For**: Docker Compose deployment (compose generation and microservices architecture)

## Quick Install

Choose the method that matches your VM environment:

### Ubuntu/Debian VMs (Recommended Method)

#### Option 1: Using Bootstrap Script (Automatic)
The easiest way - the bootstrap script now installs ruamel.yaml automatically:

```bash
cd /path/to/PrintFarmer
bash scripts/bootstrap-ubuntu.sh
```

This will automatically:
1. ✅ Install Python3 (if not present)
2. ✅ Install ruamel.yaml via apt-get (preferred)
3. ✅ Fall back to pip if apt-get package unavailable

#### Option 2: Manual Installation (If Bootstrap Not Available)

**Method A: System Package (Recommended)**
```bash
sudo apt-get update
sudo apt-get install -y python3-ruamel.yaml
```

**Method B: Using pip**
```bash
# Install pip first (if not present)
sudo apt-get install -y python3-pip

# Then install ruamel.yaml
pip install ruamel.yaml
```

**Method C: Using pip for current user only (No sudo needed)**
```bash
python3 -m pip install --user ruamel.yaml
```

### macOS VMs

#### Option 1: Using Bootstrap Script (Automatic)
```bash
cd /path/to/PrintFarmer
bash scripts/bootstrap-macos.sh
```

This will:
1. ✅ Install Python3 via Homebrew (if not present)
2. ✅ Install ruamel.yaml via pip

#### Option 2: Manual Installation

**With Homebrew**
```bash
# Install Python if needed
brew install python3

# Install ruamel.yaml
pip3 install ruamel.yaml
```

**Or directly with pip**
```bash
python3 -m pip install --user ruamel.yaml
```

### Windows VMs

#### Option 1: Using Bootstrap Script (Automatic - Requires PowerShell Admin)
```powershell
# Run as Administrator
cd C:\path\to\PrintFarmer
powershell -ExecutionPolicy Bypass -File scripts/bootstrap-windows.ps1 -Elevate
```

This will:
1. ✅ Install Python3 via winget (if not present)
2. ✅ Install ruamel.yaml via pip

#### Option 2: Manual Installation

**Download and Install Python**
1. Go to https://www.python.org/downloads/
2. Download Python 3.12 or later
3. Run installer, **CHECK: "Add python.exe to PATH"**
4. Restart your terminal

**Then install ruamel.yaml**
```powershell
# In PowerShell (or Command Prompt)
python -m pip install ruamel.yaml
```

## Verification

After installation, verify it's working:

```bash
python3 -c "from ruamel.yaml import YAML; print('✓ ruamel.yaml is installed')"
```

Should output:
```
✓ ruamel.yaml is installed
```

**If you get an error**, the module is NOT installed. Go back to the installation step above.

## Testing the Deployment

After installing ruamel.yaml, run the tests to confirm everything works:

```bash
cd /path/to/PrintFarmer
bash tests/run-deployment-tests.sh
```

Should show:
```
✓ SUCCESS compose-generator tests passed
✓ ALL TESTS PASSED - Ready to commit!
```

If you see errors about ruamel.yaml, the installation didn't work. Verify with:
```bash
python3 -c "from ruamel.yaml import YAML"
echo $?  # Should print 0 if successful
```

## Troubleshooting

### "Command not found: python3"
- **Ubuntu/Debian**: `sudo apt-get install python3`
- **macOS**: `brew install python3`
- **Windows**: Download from https://www.python.org/downloads/

### "ModuleNotFoundError: No module named 'ruamel'"
- The Python module is not installed
- Try: `python3 -m pip install ruamel.yaml --upgrade`
- If pip not found: `sudo apt-get install python3-pip` (Ubuntu/Debian)

### "Permission denied" when installing
- Use `--user` flag: `python3 -m pip install --user ruamel.yaml`
- OR use sudo: `sudo pip3 install ruamel.yaml` (less secure)

### Multiple Python versions installed
- Make sure you're using Python 3.6+: `python3 --version`
- If you have both `python` and `python3`, use: `python3 -m pip install ruamel.yaml`

## Why This Dependency Matters

The Docker Compose generator script requires `ruamel.yaml` to:
1. **Properly parse YAML** - Understand the structure of compose files
2. **Maintain indentation** - Keep service definitions correctly nested
3. **Preserve formatting** - Avoid generating malformed YAML

**Without it**, your deployments will fail with:
```
services must be a mapping
```

This is because the fallback text processing can't properly handle YAML indentation.

## Automated Bootstrap Usage

For new VMs or CI/CD pipelines, you can run the entire bootstrap with one command:

```bash
# Download and run bootstrap
curl -fsSL https://raw.githubusercontent.com/jpapiez/PrintFarmer/main/scripts/bootstrap-ubuntu.sh | bash
```

Or for existing clones:
```bash
# From your PrintFarmer directory
./scripts/bootstrap-ubuntu.sh
```

## CI/CD Integration

If you're setting up CI/CD (GitHub Actions, GitLab CI, etc.), add to your pipeline:

**For Ubuntu/Debian runners:**
```yaml
- name: Install Python dependencies
  run: |
    sudo apt-get update
    sudo apt-get install -y python3-ruamel.yaml
```

**For macOS runners:**
```yaml
- name: Install Python dependencies
  run: |
    brew install python3
    pip3 install ruamel.yaml
```

**For Windows runners:**
```powershell
- name: Install Python dependencies
  run: |
    python -m pip install ruamel.yaml
```

## See Also

- [ruamel.yaml Documentation](https://yaml.readthedocs.io/)
- [Python Package Index](https://pypi.org/project/ruamel.yaml/)
- `docs/RUAMEL_YAML_DEPENDENCY.md` - Detailed technical explanation
- `scripts/bootstrap-*.sh` - Automated setup scripts
