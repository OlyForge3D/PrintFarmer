# Deployment Hardware Guide

This guide helps you choose the right hardware for your PrintFarmer deployment based on your print farm size and operational requirements.

## Quick Hardware Recommendations

| Farm Size | Recommended Hardware | Budget Range | Best For |
|-----------|---------------------|--------------|----------|
| **1–10 printers** | Raspberry Pi 4 (8GB) or NUC | $150–400 | Home labs, maker spaces, small studios |
| **10–50 printers** | Mini PC / NUC (16GB) | $400–800 | Small production facilities, workshops |
| **50–200 printers** | Server / VM (32GB+) | $1,000–3,000 | Medium production, multi-location |
| **200+ printers** | Server Cluster / K8s | Custom | Large production, distributed farms |

## Hardware Tiers

### Tier 1: Raspberry Pi 4 (Small Farm: 1–10 Printers)

**When to choose:** 
- Hobby/maker spaces, educational settings
- Limited budget and power constraints
- Colocated printers (same location)
- Willing to disable advanced features

**Recommended Specs:**
- **Model:** Raspberry Pi 4 Model B
- **RAM:** 8GB (4GB minimum, 8GB strongly recommended)
- **Storage:** USB 3.0 SSD (256GB–512GB) — **DO NOT USE SD CARD** (see Storage section)
- **Network:** Gigabit Ethernet (or WiFi 5 if near router)
- **Power:** 5V 3A USB-C supply (or PoE)

**Product Examples:**
- [Raspberry Pi 4 8GB Kit](https://www.raspberrypi.com/products/raspberry-pi-4-model-b/)
- [Samsung T7 Shield 1TB SSD](https://www.samsung.com/us/computing/memory-storage/portable-solid-state-drives/portable-ssd-t7-shield-mu-pe1t0s-am/) (~$80)
- [Kingston Nucleum USB Hub + Ethernet](https://www.kingston.com/) for stable connectivity

**Resource Constraints:**
- **RAM:** Tight for 10+ printers; disable monitoring to free up ~200MB
- **CPU:** Single-core performance adequate; UI may lag under high load
- **Network:** WiFi 5 acceptable for camera streams (<3 Mbps per printer)

**Recommended Configuration (Pi 4):**
```bash
# Deployment profile for Pi
export DB_PROVIDER=sqlite
export ENABLE_MONITORING=false
export ENABLE_TELEMETRY=false
export ENABLE_ORCA_WORKER=false
export ORCA_WORKER_COUNT=0
export ENABLE_DISCOVERY=true
./scripts/deploy-docker.sh --non-interactive
```

**Limitations on Pi:**
- ❌ 3D model file slicing (native libs not available on ARM)
- ❌ Model file upload and thumbnail generation
- ⚠️  Camera streams may lag with 5+ printers on WiFi
- ⚠️  SQLite locks under write-heavy workloads (use PostgreSQL if affordable)

**Tips for Pi Success:**
1. **Use USB SSD instead of SD card** — Database file corruption is the #1 Pi failure mode
2. **Disable monitoring stack** — Frees ~200–300MB RAM
3. **Run slicer workers on separate machine** — Pi CPU too slow for OrcaSlicer
4. **Use Ethernet, not WiFi** — Network latency impacts real-time status updates
5. **Monitor disk space** — Set up log rotation to prevent disk fills
6. **Keep printers on same subnet** — WiFi discovery requires same network broadcast domain

---

### Tier 2: Mini PC / NUC (Medium Farm: 10–50 Printers)

**When to choose:**
- Small to medium production workshops
- Multiple locations (single hub)
- Want all features enabled
- Space/power constraints (vs. rack server)

**Recommended Specs:**
- **Model:** Intel NUC 11–14 Pro / AMD equivalent
- **Processor:** Intel Core i5–i7 (11th gen+) or Ryzen 5–7
- **RAM:** 16GB minimum, 32GB for 40+ printers
- **Storage:** 512GB–1TB NVMe SSD
- **Network:** Dual Gigabit Ethernet preferred
- **Power:** 65W or less (fanless preferred for reliability)

**Product Examples:**
- [Intel NUC 14 Pro (Core i7)](https://www.intel.com/content/www/us/en/products/details/nuc/kits/nuc14pro.html) (~$600–800)
- [ASUS PN50](https://www.asus.com/us/machines/pn50/) (AMD Ryzen, fanless, ~$400)
- [Minix Neo N42C](https://www.minix.com.hk/) (budget fanless, ~$200)

**Resource Profile:**
- **RAM:** Comfortable for 50 printers; standard stack fits in 16GB
- **CPU:** Handles real-time status updates and concurrent jobs smoothly
- **Network:** Dual Ethernet allows separation of discovery traffic

**Recommended Configuration (NUC):**
```bash
# Standard deployment for NUC
export DB_PROVIDER=postgres
export ENABLE_MONITORING=true
export ENABLE_TELEMETRY=true
export ENABLE_ORCA_WORKER=true
export ORCA_WORKER_COUNT=2
export ENABLE_DISCOVERY=true
./scripts/deploy-docker.sh --non-interactive
```

**All Features Available:**
- ✅ Model file slicing (OrcaSlicer workers)
- ✅ Full monitoring stack (Prometheus + Grafana)
- ✅ Telemetry and distributed tracing
- ✅ Network discovery with concurrent probes
- ✅ Multiple concurrent dispatch operations

**Notes:**
- Consider PostgreSQL over SQLite for write-heavy deployments
- Fanless models preferred for 24/7 reliability
- Dual Ethernet useful for discovery traffic isolation

---

### Tier 3: Server / VM (Large Farm: 50–200+ Printers)

**When to choose:**
- Production facilities with 50+ printers
- Multiple buildings or locations
- High reliability requirements
- Need for distributed slicing

**Recommended Specs:**
- **Processor:** Intel Xeon / AMD EPYC (4–8 cores minimum)
- **RAM:** 32GB–64GB (1GB per 10 printers as rough guide)
- **Storage:** 2TB+ NVMe SSD (fast I/O for job queue)
- **Network:** Redundant Gigabit or 10GbE
- **Power:** Redundant PSU, UPS protection
- **Environment:** Rack-mountable, temperature-controlled

**Product Examples (Physical Servers):**
- [Dell PowerEdge R6515](https://www.dell.com/en-us/shop/cty/pdp/spd/poweredge-r6515) (~$2,000–4,000)
- [Supermicro A+ Server 1114S-TNRT](https://www.supermicro.com/) (~$1,500–2,500)

**Product Examples (Cloud VM):**
- AWS EC2: `m6i.2xlarge` (8 vCPU, 32GB RAM, ~$300/month)
- Hetzner: Dedicated vServer (16 cores, 64GB RAM, ~$100/month)
- Azure: `Standard_D8s_v4` (8 vCPU, 32GB RAM, ~$350/month)

**Resource Profile:**
- **RAM:** 1GB per 10 printers (conservative estimate)
  - 50 printers → 5GB minimum
  - 100 printers → 10GB minimum
  - 200 printers → 20GB minimum
- **CPU:** 4+ cores handles parallel dispatch scoring and API requests
- **Storage:** G-code archival grows 5–20GB/month (50+ printers)

**Recommended Configuration (Server):**
```bash
# Production server deployment
export DB_PROVIDER=postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_MONITORING=true
export ENABLE_TELEMETRY=true
export ENABLE_ORCA_WORKER=true
export ORCA_WORKER_COUNT=4
export ENABLE_DISCOVERY=true
export SCAN_INTERVAL_SECONDS=300
./scripts/deploy-docker.sh --non-interactive
```

**For High Availability:**
- Deploy PrintFarmer to Kubernetes cluster
- Use managed PostgreSQL (AWS RDS, Azure Database)
- Implement multi-region redundancy for critical printers
- Set up log aggregation (ELK stack, Loki)

---

## Service Resource Matrix

This table shows approximate resource consumption for each deployed service. Numbers are per-instance (e.g., each OrcaSlicer worker) at moderate load (30 printers).

| Service | RAM | CPU | Disk | Required? | Scaling Notes |
|---------|-----|-----|------|-----------|---------------|
| **API Server** | 150–300MB | 0.5–1.0 core | — | ✅ Yes | Grows ~5MB per 10 printers; handle 100+ with 2 cores |
| **React Frontend** | 50MB | 0.1 core | — | ✅ Yes | Static assets; CPU only for serving |
| **PostgreSQL** | 300–500MB | 0.5–1.0 core | 10GB–50GB+ | ⚠️ Optional | See Storage section; grows ~100–200MB per 10 printers |
| **SQLite** | — | — | 100MB–5GB | ⚠️ Optional | Embedded; no separate container; suitable for <15 printers |
| **Prometheus** | 200–500MB | 0.2–0.5 core | 20GB–100GB | ❌ Optional | Retention 200h; grows with scrape interval |
| **Grafana** | 100–200MB | 0.1–0.2 core | 1GB | ❌ Optional | Dashboard only; minimal compute |
| **OrcaSlicer Worker** | 512MB–1GB | 1.0–2.0 cores | 5GB temp | ❌ Optional | Scale 1 worker per 15–20 concurrent slices; slower on shared CPU |
| **Printer Discovery** | 64–128MB | 0.1–0.2 core | 50MB | ✅ Optional | Network I/O bound; scales with subnet size |
| **Nginx Proxy** | 50–100MB | 0.1 core | — | ✅ Yes | Static overhead; minimal per-connection cost |
| **Slicer Host** | 1GB | 1.0 core | 10GB | ❌ Optional | Runs PrusaSlicer; CPU-intensive per slice job |

**Key Insights:**
- **API growth is sublinear:** 20 printers → 150MB, 100 printers → 250MB
- **Database dominates storage:** SQLite suitable for <15 printers; PostgreSQL required for 20+
- **OrcaSlicer workers are CPU-hungry:** 1 worker per 15–20 concurrent users; disable on Pi
- **Discovery service lightweight:** Safe to enable on all tiers
- **Monitoring stack optional:** Saves 300–700MB; disable on Pi or constrained hardware

---

## Storage Recommendations

### Database Selection

**SQLite (File-Based)**
- ✅ **Best for:** 1–15 printers, single-server deployments, no DB admin needed
- ✅ **Advantages:** Zero setup, embeds in API container, works offline
- ❌ **Limitations:** Single-writer lock; poor under concurrent writes (30+ printers with real-time updates)
- **Approximate Size:** 100MB → 500MB for 10 printers, 1–2GB for 20 printers
- **Disk Space:** Allocate 5–10GB for growth and log accumulation

**PostgreSQL (Client-Server)**
- ✅ **Best for:** 20+ printers, distributed teams, concurrent workloads
- ✅ **Advantages:** Multi-user, ACID transactions, WAL replication, mature tooling
- ⚠️  **Setup:** Requires separate container/server; learning curve for backup/recovery
- **Approximate Size:** 300MB initial, grows 100–150MB per 10 printers
- **Disk Space:** Allocate 50GB for 50 printers (including logs and backups)

**SQL Server / MySQL**
- ✅ **Supported** via multi-provider abstraction
- ⚠️  **Not recommended** for small farms (SQL Server licensing expensive; MySQL less mature for this workload)

**Recommendation:**
- **Pi/NUC (1–20 printers):** Start with SQLite; upgrade to PostgreSQL if write contention occurs
- **Server (50+ printers):** Deploy PostgreSQL immediately with automated backup strategy

### Storage Device for Pi (Critical!)

**⚠️ DO NOT USE SD CARD FOR DATABASE**

| Storage Type | Durability | Speed | Cost | Recommendation |
|--------------|-----------|-------|------|-----------------|
| **SD Card (Class 10)** | ❌ Poor (write degradation) | 20–90 MB/s | $15–30 | ❌ Never use for DB |
| **USB 3.0 SSD** | ✅ Excellent (NAND wear-leveling) | 400+ MB/s | $50–100 | ✅ **Recommended** |
| **USB 3.0 HDD** | ✅ Good (mechanical, slower) | 100–150 MB/s | $40–80 | ✅ Acceptable |
| **NVMe (via USB adapter)** | ✅ Excellent | 500+ MB/s | $60–150 | ✅ Best performance |

**Why Pi needs USB storage:**
- SD cards have **poor random write performance** (SQLite uses random I/O)
- Wear-leveling in SD cards assumes sequential writes (not applicable to databases)
- **Database corruption** is common after 1–2 years on SD cards
- USB SSDs have **hardware wear-leveling** designed for databases

**Recommended Pi Storage Setup:**
```bash
# 1. Purchase USB 3.0 SSD (Samsung T7 or similar, 256GB+)
# 2. Format as ext4
# 3. Mount as /mnt/ssd
# 4. Configure deployment to use SSD for volumes:
export EXTERNAL_DATABASE_PATH=/mnt/ssd/printfarmer-database
export EXTERNAL_APP_DATA_PATH=/mnt/ssd/printfarmer-app
./scripts/deploy-docker.sh --non-interactive
```

**Disk Space Planning:**
- **Application code:** 500MB
- **SQLite database:** 100MB → 1GB (10 printers → 20 printers)
- **G-code files:** 5–50MB per job (archive old files monthly)
- **Logs:** 100–500MB/month (rotate weekly)
- **Buffer (20% free space):** Important for database performance

Allocate **256GB minimum** for Pi; **512GB recommended**.

### G-Code Storage Sizing

**Typical G-Code File Sizes:**
- **Small prints:** 1–5MB (lithophanes, single-color mini models)
- **Medium prints:** 5–50MB (20-hour prints, multi-color)
- **Large prints:** 50–200MB (multi-day prints, large models)

**Monthly G-Code Accumulation (by farm size):**
| Farm Size | Avg Jobs/Day | Typical Monthly | Recommended Retention |
|-----------|------|---------|------|
| 5 printers | 50 jobs/day | 5–10GB | 30 days (1–2 archive tapes) |
| 20 printers | 200 jobs/day | 20–40GB | 30 days (2–4 archive copies) |
| 100+ printers | 1,000+ jobs/day | 100–200GB | 14 days online + archive off-site |

**Archival Strategy:**
- Keep **last 30 days online** (fast re-print capability)
- Archive **older files to tape/S3** monthly (cheap long-term storage)
- Implement automated rotation to prevent disk fills

---

## Network Requirements

### Bandwidth Per Printer

**Typical network consumption at steady state (1 printer printing):**

| Data Type | Bandwidth | Notes |
|-----------|-----------|-------|
| **Real-time status (SignalR)** | 5–50 Kbps | Temperature, position, job progress; heartbeat every 2–5 seconds |
| **Camera stream (H.264 @ 1 fps)** | 100–500 Kbps | IP camera RTSP; depends on resolution and compression |
| **G-code upload** | 1–5 Mbps | One-time; 50MB file = 10–50 seconds |
| **Log streaming** | 10–100 Kbps | Diagnostic logs; minimal if not debugging |
| **Total sustained** | ~500 Kbps – 1 Mbps | **Per printer with camera** |

**Network Planning by Farm Size:**

| Farm Size | Total Bandwidth | Link Required | Recommendation |
|-----------|---------|---------------|-----------------|
| **5 printers** | 2.5–5 Mbps | 10 Mbps adequate | WiFi 5 (2.4/5 GHz) acceptable |
| **20 printers** | 10–20 Mbps | 100 Mbps required | Gigabit Ethernet; WiFi too congested |
| **100+ printers** | 50–100 Mbps | Gigabit required | Consider 10GbE for discovery traffic |

### Network Architecture

**Same-Subnet Requirement for Printer Discovery:**

PrintFarmer discovers printers via broadcast probes (UDP) and TCP port scans. Printers must be **on the same subnet as the PrintFarmer server** (or accessible via configured CIDR ranges).

**Configuration:**
```bash
# During deployment, specify printer subnets
export DISCOVERY_SUBNETS="192.168.1.0/24,192.168.2.0/24"
./scripts/deploy-docker.sh --non-interactive
```

**Multi-Location Setup (across subnets):**
- Deploy **one PrintFarmer instance per building** if subnets don't route
- Or use **VPN/tunnel** to make remote printers appear local
- Or **manually configure printer IPs** (skip discovery for remote printers)

**WiFi vs. Ethernet:**

| Connection | Latency | Reliability | Bandwidth | Recommendation |
|-----------|---------|------------|-----------|-----------------|
| **Gigabit Ethernet** | <5ms | >99.9% | 1000 Mbps | ✅ **Best** |
| **WiFi 6 (802.11ax)** | 10–50ms | 95% | 600+ Mbps | ✅ Good for <20 printers |
| **WiFi 5 (802.11ac)** | 20–100ms | 90% | 300 Mbps | ⚠️  Marginal for cameras |
| **WiFi 4 (802.11n)** | 50–200ms | 80% | 100 Mbps | ❌ Not recommended |

**Recommendation:**
- **Printers:** Always Ethernet (if available); WiFi acceptable for mobile units
- **PrintFarmer server:** Gigabit Ethernet for stability
- **Camera streams:** Separate switch from printer traffic if possible (WiFi cameras can saturate network)

---

## Deployment Profiles

PrintFarmer supports three deployment profiles with different resource footprints:

### Profile 1: `lite` (Raspberry Pi)
**Suitable for:** 1–10 printers, limited resources, hobby/education

**Enabled Services:**
- ✅ API Server
- ✅ React Frontend
- ✅ SQLite Database (embedded)
- ✅ Printer Discovery
- ❌ Monitoring (Prometheus/Grafana)
- ❌ Telemetry (OpenTelemetry)
- ❌ Slicer Workers (OrcaSlicer)

**Resource Footprint:** ~600MB RAM, ~1GB disk

**Deployment:**
```bash
export DB_PROVIDER=sqlite
export ENABLE_MONITORING=false
export ENABLE_TELEMETRY=false
export ENABLE_ORCA_WORKER=false
./scripts/deploy-docker.sh --non-interactive
```

---

### Profile 2: `standard` (NUC / Mini PC)
**Suitable for:** 10–50 printers, small team, all core features

**Enabled Services:**
- ✅ API Server + React Frontend
- ✅ PostgreSQL Database
- ✅ Printer Discovery
- ✅ Monitoring (Prometheus/Grafana lite)
- ✅ OrcaSlicer Worker (1–2 instances)
- ⚠️  Telemetry (optional)

**Resource Footprint:** ~1.5GB RAM, ~20GB disk

**Deployment:**
```bash
export DB_PROVIDER=postgres
export ENABLE_MONITORING=true
export ENABLE_TELEMETRY=false
export ENABLE_ORCA_WORKER=true
export ORCA_WORKER_COUNT=2
./scripts/deploy-docker.sh --non-interactive
```

---

### Profile 3: `full` (Server / VM)
**Suitable for:** 50–200+ printers, production deployment, complete observability

**Enabled Services:**
- ✅ API Server + React Frontend
- ✅ PostgreSQL Database (with replication)
- ✅ Printer Discovery (optimized for large subnets)
- ✅ Monitoring (full Prometheus + Grafana)
- ✅ Telemetry (OpenTelemetry collector)
- ✅ OrcaSlicer Workers (4+ instances for parallel slicing)
- ✅ pgAdmin (database management)
- ✅ Slicer Host (PrusaSlicer support)

**Resource Footprint:** ~3–5GB RAM, ~50GB+ disk

**Deployment:**
```bash
export DB_PROVIDER=postgres
export ENABLE_DISTRIBUTED_SLICING=true
export ENABLE_MONITORING=true
export ENABLE_TELEMETRY=true
export ENABLE_ORCA_WORKER=true
export ORCA_WORKER_COUNT=4
export ENABLE_DISCOVERY=true
export ENABLE_PGADMIN=true
./scripts/deploy-docker.sh --non-interactive
```

---

## Deployment Modes: Monolith vs. Microservices

PrintFarmer supports two deployment architectures, each optimized for different scenarios:

### Monolith Mode (`DEPLOYMENT_MODE=monolith`)

**What it is:** Single container serving both API backend and React frontend. The API serves static frontend files from its wwwroot directory.

**When to use:**
- ✅ Raspberry Pi and ARM64 systems (minimal resource overhead)
- ✅ Single-machine deployments (no load balancing needed)
- ✅ Simplified networking (single port, no reverse proxy)
- ✅ Low-latency environments (same container = no network hops)

**Advantages:**
- 🚀 **Faster:** Direct same-process communication between API and frontend
- 📦 **Minimal:** Single container, no nginx/proxy overhead (~50MB smaller image)
- 🔧 **Simpler:** No CORS configuration, no separate frontend deployment
- 💰 **Cost-effective:** Ideal for budget deployments (Pi, low-resource VMs)

**Limitations:**
- ❌ No horizontal scaling (single container can't be load-balanced)
- ❌ No separate frontend updates (requires full rebuild)
- ❌ Higher API container load (serving static files + API requests)

**Monolith Mode Configuration:**

```bash
# Set deployment mode
export DEPLOYMENT_MODE=monolith

# Minimal Pi configuration
export DB_PROVIDER=sqlite
export ENABLE_MONITORING=false
export DEPLOYMENT_MODE=monolith

# Deploy
./scripts/deploy-docker.sh --non-interactive
```

**Running the Monolith:**

```bash
# Pull monolith image from GHCR
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest

# Run directly
docker run -d \
  --name printfarmer \
  -p 5000:5000 \
  -e DB_PROVIDER=sqlite \
  -e DEPLOYMENT_MODE=monolith \
  -v printfarmer-data:/app/data \
  ghcr.io/olyforge3d/printfarmer-monolith:latest

# Application is now at http://localhost:5000
```

---

### Microservices Mode (Default)

**What it is:** Separate containers for API backend, React frontend (nginx), and optional services (monitoring, discovery, slicers).

**When to use:**
- ✅ Production deployments requiring horizontal scaling
- ✅ Multi-machine setups (Kubernetes, Docker Swarm)
- ✅ Separate frontend CI/CD pipelines
- ✅ Load-balanced API tier

**Advantages:**
- 📈 **Scalable:** Each tier (API, frontend, workers) scales independently
- 🔄 **Flexible:** Update frontend without rebuilding API
- 📊 **Observable:** Separate logs, metrics, health checks per service
- 🔐 **Isolatable:** Each service has its own security context

**Microservices Configuration:**

```bash
# Default (no DEPLOYMENT_MODE needed)
# Or explicitly:
export DEPLOYMENT_MODE=microservices

# Standard configuration
export DB_PROVIDER=postgres
export ENABLE_MONITORING=true

# Deploy
./scripts/deploy-docker.sh --non-interactive
```

**Running Microservices:**

```bash
# Pull images
docker pull ghcr.io/olyforge3d/printfarmer-api:latest
docker pull ghcr.io/olyforge3d/printfarmer-frontend:latest

# Start with Docker Compose (recommended)
docker compose -f docker-compose.yml up -d

# Or manually with docker run (complex, not recommended)
```

---

## Deployment Profiles by Farm Size

| Profile | Hardware | Deployment Mode | Database | Container Count | Best For | Annual Cost |
|---------|----------|-----------------|----------|-----------------|----------|------------|
| **Lite** | Raspberry Pi 4 (8GB) | Monolith | SQLite | 1 | Home lab, maker space (1–10 printers) | ~$300 |
| **Standard** | NUC / Mini PC (16GB) | Microservices | SQLite or Postgres | 3–4 | Workshop, studio (10–50 printers) | ~$850 |
| **Full** | Server / VM (32GB+) | Microservices | Postgres or SQL Server | 5–8 | Production farm (50–200+ printers) | ~$2,000 |

---

## Raspberry Pi Quick Start (Lite Profile)

### Step 1: Hardware Setup

1. **Buy:** Raspberry Pi 4 (8GB RAM) + USB 3.0 SSD (256GB)
2. **Image:** Install Ubuntu Server 22.04 LTS (64-bit ARM)
   ```bash
   # Use Raspberry Pi Imager to write Ubuntu image to SSD
   # https://www.raspberrypi.com/software/
   ```
3. **Boot:** Connect Ethernet, power on, wait 2 minutes
4. **SSH:** `ssh ubuntu@raspberrypi.local`

### Step 2: Prepare System

```bash
# Update packages
sudo apt update && sudo apt upgrade -y

# Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh

# Add user to docker group (no sudo needed)
sudo usermod -aG docker $USER
newgrp docker

# Clone PrintFarmer
git clone https://github.com/OlyForge3D/printfarmer.git
cd PrintFarmer
```

### Step 3: Deploy Monolith

```bash
# Automatic deployment (interactive prompts)
./scripts/deploy-docker.sh

# Or silent deployment with defaults
export DEPLOYMENT_MODE=monolith
export DB_PROVIDER=sqlite
./scripts/deploy-docker.sh --non-interactive
```

### Step 4: Access PrintFarmer

- **URL:** `http://<pi-ip>:5000` (or `http://raspberrypi.local:5000`)
- **Default login:** admin@printfarmer.local / PrintFarmer2024!

### Monitoring Pi Health

```bash
# Check resource usage (CPU, RAM, disk)
docker stats printfarmer

# View API logs
docker logs -f printfarmer

# Monitor database growth (SQLite)
du -h printfarmer-data/

# If Pi runs low on memory
docker update --memory 400m printfarmer
```

### Pi Deployment Checklist

- [ ] Pi 4 with 8GB RAM (minimum; Pi 5 recommended)
- [ ] USB 3.0 SSD (NOT SD card)
- [ ] Ethernet connected (not WiFi)
- [ ] Power supply rated for 3A+
- [ ] Ubuntu 22.04 LTS (64-bit) installed
- [ ] Docker installed and running
- [ ] `DEPLOYMENT_MODE=monolith` set for single container
- [ ] `DB_PROVIDER=sqlite` for embedded database
- [ ] Port 5000 accessible from your network

---

## Docker Deployment with `deploy-docker.sh`

### First-Time Deployment

```bash
cd /path/to/PrintFarmer
chmod +x scripts/deploy-docker.sh
./scripts/deploy-docker.sh
```

The script will:
1. **Detect your OS** (macOS, Linux, Windows WSL2)
2. **Prompt for settings:**
   - Database provider (SQLite, PostgreSQL)
   - HTTP port (default 80)
   - Optional services (monitoring, telemetry, slicing workers)
   - Number of OrcaSlicer workers (1–4 recommended)
3. **Save configuration** to `.deploy-config` (source control ignored)
4. **Generate `.env` files** with derived settings
5. **Build and start containers** via Docker Compose
6. **Validate deployment** with health checks

### Re-Deploy with Saved Settings

```bash
./scripts/deploy-docker.sh --non-interactive
# Uses settings from .deploy-config
# No prompts; instant deployment
```

### Change Deployment Settings

```bash
# Edit config
nano .deploy-config

# Or override via environment
export ORCA_WORKER_COUNT=4
export ENABLE_MONITORING=true
./scripts/deploy-docker.sh --non-interactive
```

### Dry-Run (Preview without deploying)

```bash
./scripts/deploy-docker.sh --dry-run
# Shows generated compose files and configuration
# No containers started
```

### Manual Profile Deployment (Advanced)

If you prefer direct Docker Compose control:

```bash
# Lite profile (Pi)
docker compose -f docker-compose.yml up -d

# Standard profile (add monitoring)
docker compose -f docker-compose.yml \
  -f scripts/docker/compose-templates/docker-compose.monitoring.lite.yml \
  -f scripts/docker/compose-templates/docker-compose.discovery.yml \
  up -d

# Full profile (add telemetry, extra workers)
docker compose -f docker-compose.yml \
  -f scripts/docker/compose-templates/docker-compose.monitoring.yml \
  -f scripts/docker/compose-templates/docker-compose.telemetry.yml \
  -f scripts/docker/compose-templates/docker-compose.discovery.yml \
  -f scripts/docker/compose-templates/docker-compose.orcaslicer-worker.yml \
  up -d
```

---

## Performance Optimization Tips

### For Raspberry Pi 4

1. **Use USB SSD for database** (see Storage section)
2. **Enable cgroup memory limits** to prevent OOM crashes
   ```bash
   # In docker-compose override
   api:
     deploy:
       resources:
         limits:
           memory: 400M
         reservations:
           memory: 200M
   ```
3. **Disable monitoring** (saves 200–300MB RAM)
   ```bash
   export ENABLE_MONITORING=false
   ```
4. **Use Ethernet over WiFi** for network stability
5. **Monitor disk space** with log rotation
   ```bash
   docker exec printfarmer-api sh -c "tail -n 10000 /app/logs/app.log > /app/logs/app.log.tmp && mv /app/logs/app.log.tmp /app/logs/app.log"
   ```
6. **Run slicer workers on separate machine** (Pi CPU too slow)

### For NUC / Mini PC

1. **Enable PostgreSQL** for concurrent write performance
2. **Tune postgres shared_buffers** (25% of RAM for 16GB)
   ```bash
   export POSTGRES_SHARED_BUFFERS="4GB"
   ```
3. **Enable monitoring** for visibility into performance
4. **Run 2–4 OrcaSlicer workers** based on CPU cores
5. **Monitor database WAL growth** (disk space indicator)

### For Server / VM

1. **Use managed PostgreSQL** (AWS RDS, Azure Database) instead of container
2. **Enable distributed slicing** across multiple worker nodes
3. **Configure Prometheus retention** (200 hours for 50GB storage)
4. **Use SSD for database WAL** (separate disk for IOPS)
5. **Set up automated backups** (daily snapshots to S3 / off-site)
6. **Monitor connectivity** to 50+ printers with discovery probes

---

## Troubleshooting

### "Out of Memory" on Raspberry Pi

**Symptoms:**
- API container restarts unexpectedly
- Database becomes unresponsive
- UI lags under normal load

**Solutions:**
1. Check available memory:
   ```bash
   docker stats printfarmer-api
   # If consistently >350MB: need Pi upgrade or feature reduction
   ```
2. Disable monitoring:
   ```bash
   export ENABLE_MONITORING=false
   ./scripts/deploy-docker.sh --non-interactive
   ```
3. Set explicit memory limits:
   ```bash
   docker update --memory 400m printfarmer-api
   ```
4. Upgrade to Pi 5 (8GB) or NUC

### Database Write Contention

**Symptoms (SQLite):**
- "database is locked" errors in API logs
- Slow UI responses with 10+ concurrent printers
- Real-time status updates lag

**Solutions:**
1. Upgrade to PostgreSQL:
   ```bash
   export DB_PROVIDER=postgres
   ./scripts/deploy-docker.sh
   ```
2. Reduce update frequency (if custom):
   ```bash
   # In API configuration
   export SIGNALR_UPDATE_INTERVAL_MS=5000  # 5 seconds instead of 2
   ```

### Printer Discovery Not Finding Printers

**Symptoms:**
- Manual printer entry required; discovery returns empty

**Solutions:**
1. Verify printer subnet in config:
   ```bash
   docker logs printfarmer-printer-discovery | grep -i subnet
   ```
2. Ensure same network (printers must be reachable from PrintFarmer host)
   ```bash
   # From Pi/NUC: ping a known printer IP
   ping 192.168.1.100
   ```
3. Configure subnets explicitly:
   ```bash
   export DISCOVERY_SUBNETS="192.168.1.0/24"
   ./scripts/deploy-docker.sh --non-interactive
   ```

### Camera Streams Slow / Timeout

**Symptoms:**
- Camera snapshot takes 5+ seconds
- Video stream constantly buffering

**Solutions:**
1. Check network bandwidth:
   ```bash
   docker exec printfarmer-api curl -s http://192.168.1.100/stream | head -c 1000000 > /dev/null
   # Measure time; should complete in 1–2 seconds for 1MB
   ```
2. Reduce camera resolution (on printer, not PrintFarmer)
3. Use Ethernet for PrintFarmer server
4. Separate printer WiFi from camera WiFi (if possible)

---

## GitHub Container Registry (GHCR) Images

All PrintFarmer container images are publicly available on GitHub Container Registry, supporting both `x86_64` and `arm64` architectures. No authentication required for pulling public images.

### Available Images

| Image | Purpose | Supported Architectures | Size |
|-------|---------|-------------------------|------|
| `ghcr.io/olyforge3d/printfarmer-monolith` | Single container (API + frontend) | linux/amd64, linux/arm64 | ~450MB |
| `ghcr.io/olyforge3d/printfarmer-api` | Backend API only | linux/amd64, linux/arm64 | ~300MB |
| `ghcr.io/olyforge3d/printfarmer-frontend` | React frontend (nginx) | linux/amd64, linux/arm64 | ~150MB |

### Quick Pull Commands

```bash
# Monolith (single container, recommended for Pi)
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest

# Or microservices (separate API + frontend)
docker pull ghcr.io/olyforge3d/printfarmer-api:latest
docker pull ghcr.io/olyforge3d/printfarmer-frontend:latest

# List available tags (versions, architectures)
docker pull ghcr.io/olyforge3d/printfarmer-monolith:latest --dry-run
```

### Image Tags

- **`latest`** — Latest production release (recommended)
- **`main`** — Latest development build from main branch
- **`v1.2.3`** — Specific release version (e.g., v1.2.3, v1.3.0)

### Multi-Architecture Support

All images support both `x86_64` and `arm64`:

```bash
# Inspect image for available platforms
docker buildx imagetools inspect ghcr.io/olyforge3d/printfarmer-monolith:latest

# Output shows:
# - linux/amd64 (Intel/AMD)
# - linux/arm64 (Raspberry Pi, ARM servers)
```

Docker automatically selects the correct architecture when pulling on ARM64 systems.

### Running from GHCR

**Monolith mode (Pi/ARM):**
```bash
docker run -d \
  --name printfarmer \
  -p 5000:5000 \
  -e DB_PROVIDER=sqlite \
  -e DEPLOYMENT_MODE=monolith \
  -v printfarmer-data:/app/data \
  ghcr.io/olyforge3d/printfarmer-monolith:latest
```

**Microservices mode:**
```bash
# Use Docker Compose (see DEPLOYMENT.md for full setup)
docker compose -f docker-compose.yml up -d
```

### Image Details

**Monolith Image Contents:**
- ASP.NET Core 10 runtime
- React 19 frontend (built, static files in wwwroot/)
- SQLite support built-in
- PostgreSQL, MySQL, SQL Server drivers
- Health checks configured
- ~450MB compressed

**When to use GHCR images directly:**
- Manual Docker deployments (vs. automated `deploy-docker.sh`)
- Custom orchestration (Kubernetes, Nomad, etc.)
- CI/CD pipelines
- Air-gapped deployments (pre-pull images before network isolation)

---

## Cost Comparison

**12-Month Operating Cost (electricity + hardware depreciation + storage):**

| Configuration | Hardware | Electricity | Storage | Total |
|---|---|---|---|---|
| **Pi 4 (8GB) + SSD** | $200 | $40 | $50 | ~**$300** |
| **Intel NUC i5** | $600 | $100 | $150 | ~**$850** |
| **AWS EC2 t3.xlarge** | — | — | — | ~**$3,600/year** |
| **Hetzner Dedicated vServer** | — | — | — | ~**$1,200/year** |

**Break-even:** Hetzner / dedicated hardware becomes cost-effective at 50+ concurrent cloud VMs.

---

## See Also

- **[Deployment Guide](./DEPLOYMENT.md)** — Complete deployment instructions
- **[Docker Deployment](./DOCKER_DEPLOYMENT.md)** — Docker-specific configurations
- **[Troubleshooting Guide](./TROUBLESHOOTING.md)** — Common issues and solutions
- **[Architecture Overview](./ARCHITECTURE.md)** — System design and data flow

---

**Last Updated:** March 2026  
**Maintained by:** Documentation Team
