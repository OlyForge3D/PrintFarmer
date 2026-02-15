# SQL Server Container Health Troubleshooting

## Problem: SQL Server Container Shows "Unhealthy" Status

When deploying PrintFarmer with SQL Server, you may see:

```
⚠️  Database service marked unhealthy
⚠️  Still waiting for DB to become available...
🔴 DATABASE HEALTH CHECK FAILED
```

This guide explains why this happens and how to diagnose and fix it.

## Why SQL Server Takes Time to Start

SQL Server requires:
1. **Initialization**: Creating system databases (~30-45 seconds)
2. **Memory allocation**: Startup memory configuration
3. **License verification**: SQL Server edition startup checks
4. **Port binding**: TCP listener startup

**First boot typically takes 60-120 seconds**, not the 15-30 seconds of other databases.

## Quick Diagnostics

### 1. Check Container Status
```bash
# See container health status
docker compose ps

# Output should show:
# database   running (healthy)   # After successful startup
# database   running (starting)  # During initialization
# database   running (unhealthy) # If something is wrong
```

### 2. View Real-Time Logs
```bash
# Watch logs as SQL Server starts
docker compose logs -f database

# Look for key messages:
# ✅ "SQL Server is now ready for client connections" - SUCCESS
# ❌ "The SA password does not meet SQL Server password policy requirements"
# ❌ "Error: Address already in use" - Port 1433 conflict
# ❌ "Insufficient memory" - Not enough RAM available
```

### 3. Check SQL Server Startup in Detail
```bash
# Stop and see detailed output
docker compose down
docker compose up database

# Watch the terminal for error messages (Ctrl+C to stop)
```

## Common Issues & Solutions

### Issue 1: SA Password Complexity Requirements

**Symptom**:
```
The SA password does not meet SQL Server password policy requirements.
Password must be at least 8 characters long and contain characters from three of the following four sets:
- Uppercase letters (A-Z)
- Lowercase letters (a-z)
- Numbers (0-9)
- Non-alphanumeric characters (!@#$%^&*)
```

**Solution**:
```bash
# Option 1: Let deploy script generate a strong password
rm .env docker-compose.override.yml
./scripts/deploy-docker.sh

# Option 2: Manually set a valid password
# Example strong password: Pfarm@2024Secure
export SQLSERVER_PASSWORD="Pfarm@2024Secure"
./scripts/deploy-docker.sh
```

### Issue 2: Port Already in Use

**Symptom**:
```
Error: Address already in use (port 1433)
```

**Solution**:
```bash
# Find what's using port 1433
sudo lsof -i :1433

# Option A: Kill the conflicting process
kill -9 <PID>

# Option B: Use a different port
docker compose down
DB_SQLSERVER_PORT=1434 ./scripts/deploy-docker.sh
```

### Issue 3: Out of Memory

**Symptom**:
```
Cannot allocate memory
SQL Server container exited
```

**Solution**:
```bash
# Check available memory
free -h
# or on macOS: vm_stat

# Free up memory by stopping other containers
docker stop <other_containers>

# Increase Docker memory allocation in Docker Desktop settings
# (Menu → Preferences → Resources → Memory)
```

### Issue 4: Disk Space Issues

**Symptom**:
```
No space left on device
I/O error while reading
```

**Solution**:
```bash
# Check disk space
df -h

# Free up space
docker system prune -a  # WARNING: removes all unused images/containers
docker volume prune     # Remove unused volumes

# Or delete old deployment files
rm docker-compose.override.yml .env docker-compose.yml 2>/dev/null
```

## SQL Server Health Check Process

The deployment script now runs automated diagnostics:

1. **Starts database container** in microservices compose
2. **Checks health status** every 3 seconds (up to 300 seconds)
3. **Tests port connectivity** as fallback (port 1433)
4. **Collects diagnostics** if health check fails:
   - Container status and logs
   - Password complexity issues
   - Port conflicts
   - Resource availability

### Automatic Diagnostics Output

When database health check fails, you'll see:

```
🔴 DATABASE HEALTH CHECK FAILED
Database did not become healthy within 300s timeout.

📊 DIAGNOSTIC INFORMATION:

Container Status:
NAME           STATUS        HEALTH
database       running       unhealthy

Recent Database Logs (last 50 lines):
<SQL Server error messages>

🔍 SQL SERVER SPECIFIC CHECKS:
- SA password complexity: Ensure MSSQL_SA_PASSWORD meets requirements
- Check if port 1433 is in use: sudo lsof -i :1433
- Verify SA_PASSWORD in .env is correct: grep MSSQL_SA_PASSWORD .env
```

## Manual Health Check Command

```bash
# Check if SQL Server is responding to connections
sqlcmd -S localhost,1433 -U sa -P 'YourPassword' -Q "SELECT @@version"

# If you don't have sqlcmd installed:
docker exec -it $(docker ps -q -f "name=database") \
  /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P 'YourPassword' \
  -Q "SELECT @@version"
```

## Why API Shouldn't Start with Unhealthy Database

**The deployment script now PREVENTS API from starting if the database is unhealthy** because:

1. **API will immediately crash** - Cannot connect to database
2. **Database errors in logs** - Confusing for debugging
3. **Wasted time** - Better to fix database first
4. **Data integrity** - No half-started services

### What Happens Now

**Before (old behavior)**:
- ❌ Database health check times out
- ❌ Script continues anyway with `|| true`
- ❌ API starts but fails to connect
- ❌ User sees cryptic connection errors

**After (current behavior)**:
- ✅ Database health check fails
- ✅ Script provides diagnostic information
- ✅ Deployment stops before starting API
- ✅ User sees clear error and troubleshooting steps

## Timeout Configuration

If your system is slow, increase the database startup timeout:

```bash
# 10 minute timeout (600 seconds) for very slow systems
DB_WAIT_TIMEOUT=600 ./scripts/deploy-docker.sh

# 5 minute timeout (300 seconds) - default
./scripts/deploy-docker.sh

# 2 minute timeout (120 seconds) - fast systems
DB_WAIT_TIMEOUT=120 ./scripts/deploy-docker.sh
```

### Recommended Timeouts

| System | Recommended Timeout | Use Case |
|--------|-------------------|----------|
| SSD, 8GB+ RAM, Fast network | 180s (3 min) | Development workstations |
| Standard HDD, 4GB RAM | 300s (5 min) | Shared servers |
| Slow HDD, <4GB RAM, Slow network | 600s (10 min) | IoT devices, Raspberry Pi |

## Advanced Diagnostics

### Check SQL Server Configuration
```bash
docker compose exec database \
  /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P 'YourPassword' \
  -Q "SELECT @@servername, @@version, @@memory_to_reserve"
```

### Monitor Container Resource Usage
```bash
docker stats database

# Watch CPU, memory, network, block I/O in real-time
```

### Full Container Inspection
```bash
docker inspect $(docker ps -q -f "name=database")

# View: network settings, mounted volumes, environment variables, health config
```

## Getting Help

If you continue to have issues:

1. **Collect diagnostic info**:
   ```bash
   echo "=== Container Status ===" && docker compose ps && \
   echo "=== Recent Logs ===" && docker compose logs database --tail 100 && \
   echo "=== Disk Space ===" && df -h && \
   echo "=== Memory ===" && free -h
   ```

2. **Include in bug report**:
   - Full error output from deployment script
   - Output of diagnostic commands above
   - Your system specs (OS, RAM, CPU, Docker version)

3. **Reference documents**:
   - [DOCKER_DEPLOYMENT.md](DOCKER_DEPLOYMENT.md) - General deployment guide
   - [MICROSERVICES_DEPLOYMENT_GUIDE.md](MICROSERVICES_DEPLOYMENT_GUIDE.md) - Network & architecture details
   - [docs/RUAMEL_YAML_DEPENDENCY.md](RUAMEL_YAML_DEPENDENCY.md) - Database setup requirements
