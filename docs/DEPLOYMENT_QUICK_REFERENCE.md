# Deployment Script - Quick Reference Card

**PrintFarmer Docker Deployment**  
**Last Updated:** October 6, 2025

---

## 🚀 Quick Commands

### First Deployment
```bash
./scripts/deploy-docker.sh
# Answer prompts → Config auto-saved to .deploy-config
# If .NET SDK not found, choose to install or skip
```

### Re-Deployment (Same Settings)
```bash
./scripts/deploy-docker.sh --non-interactive
# Uses .deploy-config → No prompts, instant deploy
```

### Re-Deployment (Update Settings)
```bash
./scripts/deploy-docker.sh
# Previous values shown as defaults → Press Enter or type new
```

### Dry Run (Test Without Deploying)
```bash
./scripts/deploy-docker.sh --dry-run
# Shows what would be deployed without actually deploying
```

---

## 📁 Key Files

| File | Purpose | Git Status |
|------|---------|------------|
| `.deploy-config` | Your saved deployment settings | ❌ Ignored |
| `.env.microservices` | Generated environment for microservices | ❌ Ignored |
| `.env.monolithic` | Generated environment for monolithic | ❌ Ignored |
| `docker-compose.host-network.yml` | Host network override (if applicable) | ❌ Ignored |
| `docker-compose.override.yml` | Database services override | ❌ Ignored |

---

## 🌐 Network Modes

### Bridge (Default - All Platforms)
```bash
export NETWORK_MODE_CHOICE=1
./scripts/deploy-docker.sh --non-interactive
```
- ✅ Works on macOS, Windows, Linux
- ❌ Limited network discovery (known IPs only)

### Host (Advanced - Linux Only)
```bash
export NETWORK_MODE_CHOICE=2
./scripts/deploy-docker.sh --non-interactive
```
- ✅ Full broadcast/multicast support
- ✅ Automatic printer discovery
- ⚠️ Linux hosts only

---

## 🔧 Common Scenarios

### Change Worker Count
```bash
# Option 1: Edit config
nano .deploy-config
# Change: ORCA_WORKER_COUNT=1 → ORCA_WORKER_COUNT=4

# Option 2: Environment override
export ORCA_WORKER_COUNT=4

# Deploy
./scripts/deploy-docker.sh --non-interactive
```

### Change Ports
```bash
export HTTP_PORT=9000
export API_PORT=5555
./scripts/deploy-docker.sh --non-interactive
# CORS automatically updates!
```

### Fresh Start
```bash
rm .deploy-config
./scripts/deploy-docker.sh
# Will prompt for all settings again
```

### View Current Config
```bash
cat .deploy-config
```

---

## 🔐 Security

### File Permissions
```bash
ls -la .deploy-config
# Should show: -rw------- (600)
```

### Sanitize for Sharing
```bash
cp .deploy-config .deploy-config.sanitized
sed -i 's/DB_PASSWORD=.*/DB_PASSWORD=REDACTED/' .deploy-config.sanitized
sed -i 's/Password=[^;]*/Password=REDACTED/g' .deploy-config.sanitized
cat .deploy-config.sanitized
```

---

## 🐛 Troubleshooting

### Config Not Loading
```bash
# Check you're in repo root
pwd  # Should be: /path/to/PrintFarmer

# Check file exists
ls -la .deploy-config

# Validate syntax
bash -n .deploy-config
```

### Port Conflicts
```bash
# Find what's using port
sudo lsof -i :5245

# Change port in config
nano .deploy-config
# Change: API_PORT=5245 → API_PORT=5246

# Redeploy
./scripts/deploy-docker.sh --non-interactive
```

### Permission Errors
```bash
# Fix config file permissions
chmod 600 .deploy-config
chown $USER:$USER .deploy-config
```

---

## 📊 Configuration Variables

### Essential
- `ARCHITECTURE` - `monolithic` or `microservices`
- `DB_PROVIDER` - `SQLite`, `Postgres`, `SqlServer`, `MySql`
- `DB_PASSWORD` - Database password
- `NETWORK_MODE` - `bridge` or `host`
- `HTTP_PORT` - Frontend port (default: 8080)
- `API_PORT` - API port (default: 5245, microservices only)

### Network Discovery
- `ENABLE_DISCOVERY` - `yes` or `no`
- `NETWORK_RANGES` - `192.168.0.0/16,10.0.0.0/8`
- `ALLOW_LOCAL_NETWORK` - `true` or `false`

### Workers
- `ENABLE_DISTRIBUTED_SLICING` - `true` or `false`
- `ENABLE_ORCA_WORKER` - `yes` or `no`
- `ORCA_WORKER_COUNT` - Number (default: 1)
- `ENABLE_PRUSA_WORKER` - `yes` or `no`
- `PRUSA_WORKER_COUNT` - Number (default: 1)
 - `DISABLE_SLICER_BUILDS` - `true` or `false` (when `true` the deploy script will force-disable Orca/Prusa worker builds and set worker counts to 0)

### Application
- `ENVIRONMENT` - `Development` or `Production`
- `ENABLE_SWAGGER` - `true` or `false`
- `ENABLE_DETAILED_LOGGING` - `true` or `false`

---

## 📚 Documentation

- **Config Persistence:** `docs/DEPLOYMENT_CONFIG_PERSISTENCE.md`
- **Host Networking:** `docs/HOST_NETWORK_DEPLOYMENT.md`
- **Quick Start:** `docs/UBUNTU_DEPLOYMENT_QUICKSTART.md`
- **Full Summary:** `docs/DEPLOYMENT_ENHANCEMENTS_SUMMARY.md`

---

## 💡 Pro Tips

### Tip 1: Multiple Environments
```bash
# Save dev config
cp .deploy-config .deploy-config.dev

# Save prod config
cp .deploy-config .deploy-config.prod

# Switch environments
cp .deploy-config.dev .deploy-config  # Use dev
cp .deploy-config.prod .deploy-config # Use prod
```

### Tip 2: Backup Before Changes
```bash
cp .deploy-config .deploy-config.backup
./scripts/deploy-docker.sh
# If issues, restore: cp .deploy-config.backup .deploy-config
```

### Tip 3: CI/CD Integration
```yaml
# .github/workflows/deploy.yml
- name: Deploy
  env:
    DB_PASSWORD: ${{ secrets.DB_PASSWORD }}
  run: |
    cat > .deploy-config << EOF
    ARCHITECTURE=microservices
    DB_PASSWORD=$DB_PASSWORD
    NETWORK_MODE=host
    # ... other settings
    EOF
    ./scripts/deploy-docker.sh --non-interactive
```

---

## ✅ Deployment Checklist

**First Deployment:**
- [ ] Run `./scripts/deploy-docker.sh`
- [ ] Answer all prompts
- [ ] Verify `.deploy-config` created
- [ ] Test deployment works
- [ ] Backup `.deploy-config`

**Re-Deployment:**
- [ ] Review `.deploy-config` settings
- [ ] Update any changed values
- [ ] Run `./scripts/deploy-docker.sh --non-interactive`
- [ ] Verify containers running
- [ ] Test application

**Production Deployment:**
- [ ] Use `ENVIRONMENT=Production`
- [ ] Set strong `DB_PASSWORD`
- [ ] Enable `NETWORK_MODE=host` (Linux)
- [ ] Configure firewall rules
- [ ] Set `ENABLE_SWAGGER=false`
- [ ] Backup `.deploy-config` securely

---

## 🎯 Common Workflows

### Update Code Only
```bash
git pull
docker compose --env-file .env.microservices down
docker compose --env-file .env.microservices build --no-cache
docker compose --env-file .env.microservices up -d
```

### Scale Workers
```bash
export ORCA_WORKER_COUNT=4
./scripts/deploy-docker.sh --non-interactive
```

### Change Database
```bash
# Stop containers
docker compose down

# Edit config
nano .deploy-config
# Change: DB_PROVIDER=SQLite → DB_PROVIDER=Postgres

# Redeploy
./scripts/deploy-docker.sh --non-interactive
```

### Enable Network Discovery
```bash
nano .deploy-config
# Change: ENABLE_DISCOVERY=no → ENABLE_DISCOVERY=yes
# Change: NETWORK_MODE=bridge → NETWORK_MODE=host
# Add: NETWORK_RANGES=192.168.0.0/16

./scripts/deploy-docker.sh --non-interactive
```

---

**Print this card and keep it handy!** 📄

**Or bookmark:** `/docs/DEPLOYMENT_QUICK_REFERENCE.md` 🔖
