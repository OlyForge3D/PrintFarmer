# Deployment Configuration Persistence

**Feature:** Automatic saving and loading of deployment settings  
**File:** `.deploy-config`  
**Last Updated:** October 6, 2025

---

## Overview

The deployment script now **automatically saves all your configuration choices** to a `.deploy-config` file. This makes re-deployment and troubleshooting incredibly simple:

- ✅ **First Deployment:** Answer questions interactively, config automatically saved
- ✅ **Re-Deployment:** Previous settings used as defaults, just press Enter to accept
- ✅ **Hands-Off:** Run `--non-interactive` with saved config for zero-touch deployment
- ✅ **Version Control:** Config file gitignored (contains passwords)
- ✅ **Troubleshooting:** Easy to share sanitized config for support

---

## How It Works

### First Deployment (Interactive)

```bash
./scripts/deploy-docker.sh
```

**What happens:**
1. Script detects no `.deploy-config` exists
2. Prompts you for all configuration (architecture, database, networking, etc.)
3. **Automatically saves** all your answers to `.deploy-config`
4. Deploys with your configuration
5. Shows success message with config file location

**Example output:**
```
💾 Saving Deployment Configuration
ℹ️  Saving configuration to .deploy-config for future deployments
✅ Configuration saved to .deploy-config
ℹ️  Re-run script to use these settings, or edit file to customize
```

### Second Deployment (Previous Settings as Defaults)

```bash
./scripts/deploy-docker.sh
```

**What happens:**
1. Script **loads** `.deploy-config`
2. Shows: "Found previous deployment configuration"
3. Uses your previous choices as **defaults** for all prompts
4. You can press Enter to accept previous values, or type new ones
5. Saves updated configuration

**Example output:**
```
🔍 Environment Detection
ℹ️  Found previous deployment configuration
✅ Loaded configuration from .deploy-config
ℹ️  Previous deployment settings will be used as defaults

Choose deployment architecture:
1. Monolithic (all-in-one container)
2. Microservices (separate API, frontend, Redis)
Default: microservices  ← Your previous choice
Choice [1-2]: 
```

Just press **Enter** to keep previous settings!

### Hands-Off Re-Deployment (Non-Interactive)

```bash
./scripts/deploy-docker.sh --non-interactive
```

**What happens:**
1. Script loads `.deploy-config`
2. Uses all saved values **without prompting**
3. Deploys automatically
4. Perfect for scripts, automation, CI/CD

**No user interaction required!**

---

## Configuration File Format

### Location
```
/path/to/PrintFarmer/.deploy-config
```

### Permissions
```bash
-rw------- (600) - Owner read/write only
```

### Example Content

```bash
# PrintFarmer Deployment Configuration
# Generated on Sun Oct  6 14:30:00 PDT 2025
# This file can be used for non-interactive deployments or as defaults for interactive mode
#
# Usage:
#   Interactive (uses these as defaults): ./scripts/deploy-docker.sh
#   Non-interactive (uses these exactly):  ./scripts/deploy-docker.sh --non-interactive
#   Dry-run:                               ./scripts/deploy-docker.sh --dry-run

# Architecture
ARCHITECTURE=microservices
COMPOSE_FILE=docker-compose.microservices.yml

# Database Configuration
DB_PROVIDER=Postgres
DB_PASSWORD=SecurePassword123!
INCLUDE_POSTGRES=yes
INCLUDE_SQLSERVER=no
INCLUDE_MYSQL=no
CONNECTION_STRING=Host=database;Database=printfarmer;Username=printfarmer;Password=printfarmer_password

# Network Configuration
ENABLE_DISCOVERY=yes
ALLOW_LOCAL_NETWORK=true
NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8
NETWORK_MODE=host
HTTP_PORT=8080
API_PORT=5245

# Application Settings
ENVIRONMENT=Production
ENABLE_SWAGGER=false
ENABLE_DETAILED_LOGGING=false

# Distributed Slicing
ENABLE_DISTRIBUTED_SLICING=true
ENABLE_ORCA_WORKER=yes
ORCA_WORKER_COUNT=2
ORCA_HOST_PORT=8081
ENABLE_PRUSA_WORKER=no
PRUSA_WORKER_COUNT=0
PRUSA_HOST_PORT=8082

# Spoolman Integration
ENABLE_SPOOLMAN=no

# Operating System (detected)
OS=linux

# Note: To use this configuration:
# 1. For interactive mode with these defaults: ./scripts/deploy-docker.sh
# 2. For non-interactive deployment:          ./scripts/deploy-docker.sh --non-interactive
# 3. To override specific values:             export VARIABLE=value && ./scripts/deploy-docker.sh --non-interactive
```

---

## Usage Scenarios

### Scenario 1: First-Time Deployment

```bash
# 1. Run script interactively
./scripts/deploy-docker.sh

# 2. Answer all prompts
Architecture: microservices
Database: PostgreSQL
Enable discovery: yes
Network mode: host
...

# 3. Config automatically saved
✅ Configuration saved to .deploy-config

# 4. Deployment completes
✅ Setup completed successfully! 🎉
```

**Result:** `.deploy-config` created with all your choices

### Scenario 2: Update Existing Deployment

```bash
# Run script again - previous settings as defaults
./scripts/deploy-docker.sh

# Previous values shown as defaults
Default: microservices  ← Just press Enter
Default: Postgres       ← Just press Enter
Default: yes            ← Or type 'no' to change

# Updated config automatically saved
```

**Result:** Quick deployment with mostly same settings, updates saved

### Scenario 3: Automated Re-Deployment

```bash
# Stop containers
docker compose down

# Re-deploy without any prompts
./scripts/deploy-docker.sh --non-interactive

# Uses .deploy-config for everything
✅ Deployment completes automatically
```

**Result:** Zero-touch re-deployment

### Scenario 4: Override Specific Settings

```bash
# Keep most settings, change one thing
export ORCA_WORKER_COUNT=4  # Scale up workers

./scripts/deploy-docker.sh --non-interactive

# Uses .deploy-config for everything except ORCA_WORKER_COUNT
```

**Result:** Partial override, rest from config

### Scenario 5: Fresh Deployment (Ignore Previous Config)

```bash
# Remove old config
rm .deploy-config

# Start fresh
./scripts/deploy-docker.sh

# Will prompt for everything again
```

**Result:** Clean slate deployment

### Scenario 6: Edit Config Manually

```bash
# Edit configuration directly
nano .deploy-config

# Change values:
# ORCA_WORKER_COUNT=2 → ORCA_WORKER_COUNT=4
# ENABLE_SWAGGER=false → ENABLE_SWAGGER=true

# Re-deploy with edited config
./scripts/deploy-docker.sh --non-interactive
```

**Result:** Manual configuration changes applied

---

## Security Considerations

### Password Storage

⚠️ **Important:** `.deploy-config` contains database passwords in plain text!

**Protection measures:**
1. ✅ File permissions: `600` (owner read/write only)
2. ✅ Gitignored: Won't be committed to version control
3. ⚠️ Backup safely: Don't share config files publicly
4. ⚠️ Server security: Protect the server where config is stored

### Safe Sharing for Support

If you need to share config for troubleshooting:

```bash
# Sanitize passwords
cp .deploy-config .deploy-config.sanitized
sed -i 's/DB_PASSWORD=.*/DB_PASSWORD=REDACTED/' .deploy-config.sanitized
sed -i 's/Password=[^;]*/Password=REDACTED/g' .deploy-config.sanitized

# Share sanitized version
cat .deploy-config.sanitized
```

### Production Best Practices

1. **Use Secrets Management:**
   ```bash
   # Don't commit .deploy-config
   # Use environment variables for passwords
   export DB_PASSWORD=$(vault read -field=password secret/printfarmer/db)
   ./scripts/deploy-docker.sh --non-interactive
   ```

2. **Restrict File Access:**
   ```bash
   # Verify permissions
   ls -la .deploy-config
   # Should be: -rw------- (600)
   
   # Fix if needed
   chmod 600 .deploy-config
   ```

3. **Regular Rotation:**
   ```bash
   # Update passwords periodically
   nano .deploy-config
   # Change DB_PASSWORD
   ./scripts/deploy-docker.sh --non-interactive
   ```

---

## Environment Variable Override

You can override **any** config file setting with environment variables:

```bash
# Config file says ORCA_WORKER_COUNT=1
# Override for this deployment:
export ORCA_WORKER_COUNT=4

./scripts/deploy-docker.sh --non-interactive

# Uses 4 workers instead of 1
# Config file NOT updated (still says 1)
```

**Priority order:**
1. **Highest:** Environment variables (`export VAR=value`)
2. **Medium:** `.deploy-config` file
3. **Lowest:** Script defaults

---

## Troubleshooting

### Config File Not Found

**Problem:** Script can't find `.deploy-config`

**Solution:**
```bash
# Check current directory
pwd
# Should be: /path/to/PrintFarmer (repo root)

# If in wrong directory:
cd /path/to/PrintFarmer
./scripts/deploy-docker.sh
```

### Config File Corrupt

**Problem:** "Bad substitution" or syntax errors

**Solution:**
```bash
# Validate config file syntax
bash -n .deploy-config

# If errors, regenerate:
mv .deploy-config .deploy-config.backup
./scripts/deploy-docker.sh
# Answer prompts to create fresh config
```

### Wrong Default Values

**Problem:** Defaults don't match what you want

**Solution:**
```bash
# Option 1: Edit config file
nano .deploy-config
# Change values, save
./scripts/deploy-docker.sh --non-interactive

# Option 2: Delete and start fresh
rm .deploy-config
./scripts/deploy-docker.sh
```

### Permission Denied

**Problem:** `Permission denied` when loading config

**Solution:**
```bash
# Fix permissions
chmod 600 .deploy-config

# Verify ownership
ls -la .deploy-config
# Should be: -rw------- user user

# If wrong owner:
sudo chown $USER:$USER .deploy-config
chmod 600 .deploy-config
```

### Config Not Updating

**Problem:** Changes not being saved

**Solution:**
```bash
# Check disk space
df -h .

# Check write permissions
touch .deploy-config-test && rm .deploy-config-test

# Run with verbose output
bash -x ./scripts/deploy-docker.sh 2>&1 | grep -A5 "Saving.*Configuration"
```

---

## Advanced Usage

### Multiple Environments

```bash
# Development config
./scripts/deploy-docker.sh
# Creates: .deploy-config

# Save as dev config
cp .deploy-config .deploy-config.dev

# Production config
rm .deploy-config
export ENVIRONMENT=Production
export ENABLE_SWAGGER=false
./scripts/deploy-docker.sh --non-interactive

# Save as prod config
cp .deploy-config .deploy-config.prod

# Switch between environments
cp .deploy-config.dev .deploy-config  # Use dev
cp .deploy-config.prod .deploy-config # Use prod
```

### CI/CD Integration

```yaml
# GitHub Actions example
- name: Deploy PrintFarmer
  env:
    DB_PASSWORD: ${{ secrets.DB_PASSWORD }}
    ARCHITECTURE: microservices
    ENABLE_DISCOVERY: yes
    NETWORK_MODE: host
  run: |
    # Create minimal config from secrets
    cat > .deploy-config << EOF
    ARCHITECTURE=microservices
    DB_PROVIDER=Postgres
    DB_PASSWORD=$DB_PASSWORD
    ENABLE_DISCOVERY=yes
    NETWORK_MODE=host
    HTTP_PORT=8080
    API_PORT=5245
    EOF
    
    # Deploy non-interactively
    ./scripts/deploy-docker.sh --non-interactive
```

### Ansible Playbook

```yaml
- name: Deploy PrintFarmer
  hosts: printfarm_servers
  tasks:
    - name: Copy deployment config
      copy:
        src: files/deploy-config.{{ env }}
        dest: /opt/printfarmer/.deploy-config
        owner: printfarmer
        group: printfarmer
        mode: '0600'
    
    - name: Run deployment script
      command:
        cmd: ./scripts/deploy-docker.sh --non-interactive
        chdir: /opt/printfarmer
      become: yes
      become_user: printfarmer
```

### Docker Volume Backup

```bash
# Backup script including config
#!/bin/bash
BACKUP_DIR="/backups/printfarmer-$(date +%Y%m%d)"
mkdir -p "$BACKUP_DIR"

# Backup config
cp .deploy-config "$BACKUP_DIR/"

# Backup docker volumes
docker compose down
tar czf "$BACKUP_DIR/volumes.tar.gz" \
  -C /var/lib/docker/volumes printfarmer_*

# Restart
docker compose up -d
```

---

## Configuration Variables Reference

### Architecture
- `ARCHITECTURE` - `monolithic` or `microservices`
- `COMPOSE_FILE` - Docker compose file path

### Database
- `DB_PROVIDER` - `SQLite`, `Postgres`, `SqlServer`, `MySql`
- `DB_PASSWORD` - Database password
- `INCLUDE_POSTGRES` - `yes` or `no`
- `INCLUDE_SQLSERVER` - `yes` or `no`
- `INCLUDE_MYSQL` - `yes` or `no`
- `CONNECTION_STRING` - Full database connection string

### Network
- `ENABLE_DISCOVERY` - `yes` or `no`
- `ALLOW_LOCAL_NETWORK` - `true` or `false`
- `NETWORK_RANGES` - Comma-separated CIDR ranges
- `NETWORK_MODE` - `bridge` or `host`
- `HTTP_PORT` - Frontend port (default: 8080)
- `API_PORT` - API port (microservices only, default: 5245)

### Application
- `ENVIRONMENT` - `Development` or `Production`
- `ENABLE_SWAGGER` - `true` or `false`
- `ENABLE_DETAILED_LOGGING` - `true` or `false`

### Slicing
- `ENABLE_DISTRIBUTED_SLICING` - `true` or `false`
- `ENABLE_ORCA_WORKER` - `yes` or `no`
- `ORCA_WORKER_COUNT` - Number of OrcaSlicer workers
- `ORCA_HOST_PORT` - OrcaSlicer host port
- `ENABLE_PRUSA_WORKER` - `yes` or `no`
- `PRUSA_WORKER_COUNT` - Number of PrusaSlicer workers
- `PRUSA_HOST_PORT` - PrusaSlicer host port

### Optional
- `ENABLE_SPOOLMAN` - `yes` or `no`
- `SPOOLMAN_BASE_URL` - Spoolman server URL
- `SPOOLMAN_PORT` - Spoolman port
- `REDIS_PERSIST` - `yes` or `no` (microservices only)
- `OVERRIDE_WORKER_ENDPOINTS` - `yes` or `no`
- `ORCA_WORKER_ENDPOINT` - Custom OrcaSlicer endpoint
- `PRUSA_WORKER_ENDPOINT` - Custom PrusaSlicer endpoint

### System
- `OS` - Detected operating system (`linux`, `macos`, `windows`)

---

## Migration from Previous Deployments

If you deployed before this feature existed:

```bash
# Your old deployment used environment variables
# Now create config file manually:

cat > .deploy-config << 'EOF'
ARCHITECTURE=microservices
DB_PROVIDER=Postgres
DB_PASSWORD=YourCurrentPassword
ENABLE_DISCOVERY=yes
NETWORK_MODE=host
HTTP_PORT=8080
API_PORT=5245
# ... add other settings
EOF

chmod 600 .deploy-config

# Now re-run script to validate and update
./scripts/deploy-docker.sh --non-interactive
```

---

## Best Practices

### ✅ DO

1. **Keep config file secure** - It contains passwords
2. **Use version-specific backups** - Save config with deployment version
3. **Test config in dev first** - Before production deployment
4. **Document custom settings** - Add comments in config file
5. **Use environment variables for secrets** - In CI/CD pipelines

### ❌ DON'T

1. **Don't commit to git** - Config is gitignored for security
2. **Don't share publicly** - Contains sensitive information
3. **Don't edit while deploying** - May cause race conditions
4. **Don't use same config across environments** - Dev vs Prod differ
5. **Don't hardcode passwords in scripts** - Use config or env vars

---

## FAQ

**Q: Where is the config file stored?**  
A: In the PrintFarmer repo root: `.deploy-config`

**Q: Is it safe to delete the config file?**  
A: Yes! Script will just prompt you again. Previous deployments unchanged.

**Q: Can I use this for multiple servers?**  
A: Yes! Copy `.deploy-config` to each server, customize as needed.

**Q: Does the config file auto-update?**  
A: Yes! Every deployment saves current settings to config file.

**Q: Can I prevent config from being saved?**  
A: Not currently. But you can delete `.deploy-config` after deployment.

**Q: What if I mix interactive and non-interactive modes?**  
A: Works fine! Interactive updates config, non-interactive uses it.

**Q: Can I use this with Docker Compose directly?**  
A: Config file is for deployment script only. Script generates `.env.*` files for Compose.

**Q: How do I reset to defaults?**  
A: Delete `.deploy-config` and run script again.

---

## Summary

**Configuration persistence provides:**

✅ **Convenience** - Set once, deploy many times  
✅ **Consistency** - Same settings across deployments  
✅ **Automation** - Non-interactive re-deployment  
✅ **Troubleshooting** - Easy config review and sharing  
✅ **Speed** - Quick re-deployment with previous settings  

**File:** `.deploy-config`  
**Location:** Repo root  
**Permissions:** `600` (secure)  
**Git Status:** Ignored (not committed)  
**Usage:** Automatic (no manual steps required)

**Just run the script - config is handled automatically!** 🚀
