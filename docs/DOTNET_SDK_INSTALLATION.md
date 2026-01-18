# .NET SDK Detection and Installation

**Feature:** Automatic .NET SDK detection with optional installation  
**Script:** `scripts/deploy-docker.sh`  
**Last Updated:** October 6, 2025

---

## Overview

The deployment script now **automatically checks for .NET SDK** and offers to install it if not found. While .NET SDK is **not required** for Docker deployment, having it installed enables:

- ✅ Local development and debugging
- ✅ Running tests before deployment
- ✅ Building custom modifications
- ✅ Using development scripts

---

## How It Works

### Automatic Detection

When you run the deployment script, it checks for .NET SDK:

```
🔍 Environment Detection
✅ Docker found: 24.0.7
✅ Docker Compose found: v2.23.0
✅ Docker daemon is running

ℹ️  Checking for .NET SDK...
```

### Scenario 1: .NET SDK Found

```
✅ .NET SDK found: 10.0.102
✅ .NET SDK version is compatible
```

Script continues normally.

### Scenario 2: .NET SDK Not Found (Interactive)

```
⚠️  .NET SDK not found
ℹ️  While Docker deployment doesn't require .NET SDK on the host,
ℹ️  having it installed allows for local development and debugging.

Would you like to install .NET SDK now? [y/N]:
```

**If you answer yes:**
```
📦 Installing .NET SDK
ℹ️  Downloading .NET installation script...
✅ Installation script downloaded
ℹ️  Installing .NET SDK 10.0...
ℹ️  This may take a few minutes...
✅ .NET SDK 10.0 installed successfully

ℹ️  To make .NET available in future sessions, add to your shell profile:
  echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc
  echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.zshrc

✅ Verified: .NET SDK 10.0.102 is now available
```

**If you answer no:**
```
ℹ️  Continuing without .NET SDK installation
ℹ️  You can install it later from: https://dotnet.microsoft.com/download
```

### Scenario 3: Non-Interactive Mode

```bash
./scripts/deploy-docker.sh --non-interactive
```

```
⚠️  .NET SDK not found
ℹ️  Skipping .NET SDK installation in non-interactive mode
ℹ️  To install manually, visit: https://dotnet.microsoft.com/download
```

Script continues with deployment.

---

## Installation Process

### What Gets Installed

- **Version:** .NET SDK 10.0 (required by PrintFarmer)
- **Location:** `$HOME/.dotnet` (user-specific, no sudo required)
- **Components:** SDK, runtime, and development tools
- **Size:** ~200-300 MB download

### Installation Steps

1. **Download** official Microsoft installation script
2. **Execute** script with channel 10.0 parameter
3. **Install** to `$HOME/.dotnet` directory
4. **Verify** installation successful
5. **Add** to PATH for current session
6. **Show** instructions for permanent PATH setup
7. **Clean up** installation script

### Platform Support

| Platform | Automatic Installation | Manual Installation |
|----------|----------------------|---------------------|
| **Linux** | ✅ Fully supported | ✅ Yes |
| **macOS** | ✅ Fully supported | ✅ Yes |
| **Windows** | ❌ Not supported | ✅ Required |

**Windows users:** Download installer from https://dotnet.microsoft.com/download

---

## Making .NET Permanent

After installation, .NET is available in your **current terminal session** but not in future sessions.

### Linux (bash)

```bash
# Add to ~/.bashrc
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.bashrc

# Reload
source ~/.bashrc
```

### macOS (zsh)

```bash
# Add to ~/.zshrc
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc
echo 'export DOTNET_ROOT="$HOME/.dotnet"' >> ~/.zshrc

# Reload
source ~/.zshrc
```

### Verify

```bash
# Check .NET is available
dotnet --version
# Should show: 10.0.102 (or similar)

# Check location
which dotnet
# Should show: /home/username/.dotnet/dotnet
```

---

## Version Requirements

### Minimum Version

PrintFarmer requires **.NET SDK 10.0 or later**

### Version Compatibility

| Version | Status | Notes |
|---------|--------|-------|
| 10.0+ | ✅ Fully supported | Recommended |
| 9.0 | ⚠️ May work | Not tested |
| 8.0 or earlier | ❌ Not supported | Will fail to build |

### Checking Your Version

```bash
dotnet --version
# Example output: 10.0.102

dotnet --list-sdks
# Example output:
# 10.0.102 [/home/user/.dotnet/sdk]
```

---

## Manual Installation

If automatic installation fails or you prefer manual installation:

### Option 1: Official Installer (Recommended)

**Download from:** https://dotnet.microsoft.com/download

1. Select **.NET 10.0**
2. Choose **SDK** (not just Runtime)
3. Download for your OS
4. Run installer
5. Follow prompts

### Option 2: Package Manager

**Ubuntu/Debian:**
```bash
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

sudo apt-get update
sudo apt-get install -y dotnet-sdk-9.0
```

**macOS (Homebrew):**
```bash
brew install --cask dotnet-sdk
```

**Windows (winget):**
```powershell
winget install Microsoft.DotNet.SDK.9
```

### Option 3: Installation Script

The script uses Microsoft's official installation script:

```bash
# Download
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh

# Install
./dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet

# Add to PATH
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

# Verify
dotnet --version
```

---

## Troubleshooting

### Installation Failed

**Problem:** Installation script fails

**Solution:**
```bash
# Check internet connectivity
ping -c 3 dot.net

# Try manual download
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
cat dotnet-install.sh  # Verify it's a shell script

# Run manually
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet --verbose
```

### Command Not Found After Installation

**Problem:** `dotnet: command not found` after installation

**Solution:**
```bash
# Check if installed
ls -la $HOME/.dotnet/dotnet

# If exists, add to PATH for current session
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

# Verify
dotnet --version

# Make permanent (choose your shell)
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc  # Linux
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc   # macOS
```

### Wrong Version Installed

**Problem:** Have .NET 8.0 but need 9.0

**Solution:**
```bash
# Install 9.0 alongside existing version
./dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet

# Or uninstall old version first
rm -rf $HOME/.dotnet
# Then reinstall
```

### Permission Denied

**Problem:** Permission errors during installation

**Solution:**
```bash
# Installation goes to $HOME/.dotnet (no sudo needed)
# If permission errors, check home directory permissions
ls -la ~ | grep .dotnet

# Fix if needed
chmod 755 $HOME/.dotnet
```

### Disk Space Issues

**Problem:** Not enough disk space

**Solution:**
```bash
# Check available space
df -h $HOME

# .NET SDK needs ~500MB
# Free up space if needed, then retry
```

---

## Docker-Only Deployment

**Important:** You do **NOT** need .NET SDK installed to deploy with Docker!

### Docker Handles Everything

The Docker build process:
1. Uses official Microsoft .NET SDK container image
2. Builds application inside container
3. Produces final runtime container
4. No host .NET SDK required

### When You Don't Need .NET SDK

- ✅ Just deploying PrintFarmer via Docker
- ✅ Not modifying source code
- ✅ Not running tests locally
- ✅ Not doing local development

**In these cases, answer "no" when prompted to install .NET SDK.**

### When .NET SDK Is Useful

- 🔧 Local development and debugging
- 🔧 Running unit tests before deploying
- 🔧 Building custom modifications
- 🔧 Using development scripts
- 🔧 Faster iteration (no Docker rebuild)

---

## CI/CD Considerations

### GitHub Actions

Most CI/CD platforms have .NET SDK pre-installed:

```yaml
# .github/workflows/deploy.yml
jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '9.0.x'
      
      - name: Verify .NET
        run: dotnet --version
      
      - name: Deploy
        run: ./scripts/deploy-docker.sh --non-interactive
```

### GitLab CI

```yaml
# .gitlab-ci.yml
image: mcr.microsoft.com/dotnet/sdk:9.0

deploy:
  script:
    - dotnet --version
    - ./scripts/deploy-docker.sh --non-interactive
```

### Jenkins

```groovy
pipeline {
    agent any
    
    tools {
        dotnetsdk 'dotnet-9.0'
    }
    
    stages {
        stage('Deploy') {
            steps {
                sh 'dotnet --version'
                sh './scripts/deploy-docker.sh --non-interactive'
            }
        }
    }
}
```

---

## Uninstallation

If you need to remove .NET SDK:

```bash
# Remove installation
rm -rf $HOME/.dotnet

# Remove from shell profile
# Linux (bash)
sed -i '/dotnet/d' ~/.bashrc

# macOS (zsh)
sed -i '' '/dotnet/d' ~/.zshrc

# Verify removed
dotnet --version
# Should show: command not found
```

---

## Security Notes

### Official Installation Script

The script downloads from Microsoft's official CDN:
- **URL:** https://dot.net/v1/dotnet-install.sh
- **Verified:** Script signature can be verified
- **Safe:** Installs to user directory (no root required)

### User-Level Installation

- ✅ No sudo/administrator required
- ✅ Installs to `$HOME/.dotnet`
- ✅ No system-wide changes
- ✅ Easy to remove

### Package Manager Installation

If using apt/yum/brew:
- ⚠️ May require sudo/administrator
- ⚠️ Installs system-wide
- ✅ Managed by OS package manager
- ✅ Automatic updates (if configured)

---

## FAQ

**Q: Do I need .NET SDK to run PrintFarmer?**  
A: No! Docker deployment works without it. .NET SDK is only useful for local development.

**Q: Will the script install .NET automatically?**  
A: Only if you answer "yes" when prompted in interactive mode. Non-interactive mode skips installation.

**Q: Can I install .NET later?**  
A: Yes! You can install manually anytime from https://dotnet.microsoft.com/download

**Q: What if I already have .NET 9.0?**  
A: The script will warn you but continue. You can install .NET 10.0 alongside.

**Q: Does this require admin/sudo?**  
A: No! Installation goes to `$HOME/.dotnet` (user directory).

**Q: Will this affect my system .NET?**  
A: No! User installation is isolated. System .NET (if any) remains unchanged.

**Q: Can I use the system-wide .NET instead?**  
A: Yes! If .NET 10.0+ is already installed system-wide, the script will detect and use it.

**Q: What about Windows?**  
A: Automatic installation not supported. Download installer from Microsoft's website.

**Q: How much disk space is needed?**  
A: About 500MB total (200-300MB download, ~500MB installed).

---

## Summary

**The deployment script now:**
- ✅ Automatically checks for .NET SDK
- ✅ Offers to install if missing (interactive mode)
- ✅ Provides clear instructions for manual installation
- ✅ Works fine without .NET SDK (Docker handles it)
- ✅ Verifies version compatibility
- ✅ Sets up PATH for current session
- ✅ Provides instructions for permanent setup

**You can:**
- Accept automatic installation (easiest)
- Decline and install manually later
- Skip entirely if only using Docker

**Remember:** .NET SDK is **optional** for Docker deployment but **recommended** for development!

---

**Installation script sourced from:** https://dot.net/v1/dotnet-install.sh  
**Manual download:** https://dotnet.microsoft.com/download  
**Documentation:** https://learn.microsoft.com/en-us/dotnet/core/install/
