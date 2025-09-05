# Docker Network Configuration for PrintFarmer

## Network Access Requirements

PrintFarmer needs to access devices on your local network for:
- **Printer Discovery**: Scanning local network for 3D printers (Moonraker, PrusaLink, etc.)
- **Spoolman Integration**: Connecting to Spoolman servers on your network
- **Direct Printer Communication**: Sending commands to printers on your LAN

## Network Configuration Options

### Option 1: Bridge Network with Host Gateway (Current Setup)
```yaml
api:
  extra_hosts:
    - "host.docker.internal:host-gateway"
  environment:
    - ALLOW_LOCAL_NETWORK=true
    - ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
```

**Benefits:**
- Maintains container isolation
- Allows selective network access
- Works cross-platform (Linux, macOS, Windows)

**Limitations:**
- May require additional configuration for complex networks
- Some network discovery features may be limited

### Option 2: Host Network Mode
```yaml
api:
  network_mode: "host"
```

**Benefits:**
- Full access to host network interfaces
- Native network discovery capabilities
- No port mapping required

**Limitations:**
- Less secure (container has full host network access)
- Not available on Docker Desktop for Mac/Windows
- May conflict with other services on host

## Current Configuration

The microservices deployment uses **Option 1** (Bridge + Host Gateway) which provides a good balance of security and functionality.

## Troubleshooting Network Issues

### Test Network Connectivity

1. **Check API can reach external networks:**
   ```bash
   curl http://localhost:5001/api/network-discovery/dynamic-ranges
   ```

2. **Test discovery against known printer IP:**
   ```bash
   curl -X POST http://localhost:5001/api/printers/discover-streaming \
     -H "Content-Type: application/json" \
     -d '{"ipRanges": ["192.168.1.100/32"]}'
   ```

3. **Verify Spoolman connectivity (if you have one):**
   ```bash
   curl http://localhost:5001/api/spoolman/test-connection \
     -H "Content-Type: application/json" \
     -d '{"baseUrl": "http://192.168.1.10:7912"}'
   ```

### Common Issues and Solutions

#### Issue: "Connection refused" when accessing local devices
**Solution:** Update your network ranges in the environment variables:
```bash
# In docker-compose.microservices.yml
- ALLOWED_NETWORK_RANGES=192.168.0.0/16,10.0.0.0/8,172.16.0.0/12,YOUR_NETWORK_RANGE
```

#### Issue: Discovery finds no printers on known network
**Solutions:**
1. **Check firewall:** Ensure ports 80, 7125 (Moonraker), 8080 (PrusaLink) are accessible
2. **Verify network range:** Use `ip route` or `ifconfig` to confirm your network range
3. **Test manually:** Try accessing printer web interface from host machine first

#### Issue: Spoolman connection fails
**Solutions:**
1. **Verify Spoolman is running:** Check `http://SPOOLMAN_IP:7912` in browser
2. **Check CORS settings:** Spoolman may need CORS configuration for container access
3. **Use host IP:** Instead of `localhost`, use your machine's actual IP address

### Advanced Network Configuration

#### Custom Network Ranges
If your network uses non-standard ranges:
```yaml
environment:
  - ALLOWED_NETWORK_RANGES=10.0.0.0/24,172.16.0.0/12
```

**Example for 10.0.0.0/24 network:**
```bash
# Update network discovery settings via API
curl -X POST http://localhost:8080/api/network-discovery/settings \
  -H "Content-Type: application/json" \
  -d '{"networkRanges":["10.0.0.0/24"],"timeoutMs":100,"maxConcurrentScans":15,"ports":[80,7125,8080]}'
```

#### Multiple Network Interfaces
For complex setups with multiple network interfaces:
```yaml
api:
  extra_hosts:
    - "host.docker.internal:host-gateway"
    - "printer-network:192.168.50.1"
    - "spoolman-network:10.1.1.1"
```

#### Host Network Override (Linux only)
For maximum compatibility on Linux servers:
```yaml
# Override in docker-compose.override.yml
api:
  network_mode: "host"
  environment:
    - ASPNETCORE_URLS=http://localhost:5001
```

## Testing Your Setup

After starting the containers, test network access:

```bash
# 1. Check services are running
docker compose -f docker-compose.microservices.yml ps

# 2. Test API connectivity
curl http://localhost:5001/healthz

# 3. Test load balancer
curl http://localhost:8080/api/printers

# 4. Test network discovery
curl -X POST http://localhost:8080/api/printers/discover-streaming \
  -H "Content-Type: application/json" \
  -d '{}'

# 5. Open web interface
open http://localhost:8080
```

## Security Considerations

- **Firewall:** Ensure your host firewall allows Docker container access
- **Network Isolation:** Consider using Docker networks to isolate different services
- **Access Control:** Use environment variables to restrict which networks the API can access
- **Monitoring:** Monitor network connections for unusual activity

## Performance Tips

- **Local DNS:** Add local device hostnames to `/etc/hosts` for faster resolution
- **Connection Pooling:** API automatically pools connections to frequently accessed devices
- **Caching:** Discovery results are cached to reduce network overhead
- **Timeouts:** Adjust network timeouts based on your network performance
