# Manual Host Network Deployment Instructions
# Run these commands on pi@10.0.0.75

# 1. Stop existing containers
cd /home/pi/src
sudo docker compose down

# 2. Deploy with host network configuration
sudo docker compose -f docker-compose.yml -f docker-compose.production.yml up --build -d

# 3. Check deployment status
sudo docker compose -f docker-compose.yml -f docker-compose.production.yml ps

# 4. Verify network configuration
sudo docker inspect printfarmer-api | grep NetworkMode

# 5. Test the deployment
curl http://10.0.0.75:8080/healthz
curl http://10.0.0.75:8081/healthz
curl http://10.0.0.75:8080/api/printers

# What this configuration does:
# ✅ API container uses host network (can discover printers on 10.0.0.x)
# ✅ Web container uses bridge network (proxies to API via host IP)
# ✅ SQL Server exposed on host port 1433 (accessible to API)
# ✅ CORS allows any origin (ALLOW_LOCAL_NETWORK=true)
# ✅ API accessible directly at http://10.0.0.75:8080
# ✅ Web proxy accessible at http://10.0.0.75:8081
