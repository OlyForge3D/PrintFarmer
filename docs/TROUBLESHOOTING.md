# Troubleshooting

Solutions to common issues in PrintFarmer.

## Connection Issues

### Printer Not Connecting

**Symptoms:** Printer shows as offline, can't send commands

**Solutions:**

1. **Check Network Connection**
   ```bash
   ping <printer-ip>
   curl http://<printer-ip>:7125/api/printer/info  # Moonraker
   curl http://<printer-ip>:8080/api/version      # PrusaLink
   ```

2. **Verify API Key**
   - Ensure API key is correctly configured
   - Test from printer's local network first
   - Check key matches printer's required format

3. **Check Firewall**
   ```bash
   # Check if ports are open
   nmap <printer-ip>
   ```

4. **Verify Backend Type**
   - Moonraker: Port 7125
   - PrusaLink: Port 8080
   - Check printer firmware matches configured type

5. **Restart Services**
   ```bash
   # Restart API server
   docker restart printfarmer-api
   
   # Restart printer firmware
   # (Usually via printer UI or SSH)
   ```

### SignalR Connection Drops

**Symptoms:** Real-time updates stop, connection indicator shows disconnected

**Solutions:**

1. **Check Network Stability**
   - Monitor network packet loss
   - Check DNS resolution
   - Verify firewall rules

2. **Increase Reconnection Timeout**
   - Edit `src/Web/ReactApp/src/services/printerSignalR.ts`
   - Increase `reconnectDelay` value
   - Increase connection timeout

3. **Restart Browser**
   - Clear cache: Ctrl+Shift+Delete
   - Reload page: Ctrl+Shift+R
   - Check browser console for errors

4. **Check Server Logs**
   ```bash
   # Docker logs
   docker logs printfarmer-api | grep -i signalr
   
   # Local logs
   tail -f ./logs/printfarmer.log
   ```

## Database Issues

### Database Connection Error

**Symptoms:** "Cannot connect to database", app won't start

**Solutions:**

1. **Check Connection String**
   ```bash
   # Verify environment variables
   echo $DB_CONNECTION_STRING
   ```

2. **Test Database Connectivity**
   ```bash
   # SQLite (file must exist or be creatable)
   ls -la ./farm.db
   
   # PostgreSQL
   psql -h localhost -U postgres -d printfarmer
   
   # SQL Server
   sqlcmd -S localhost -U sa -P <password>
   
   # MySQL
   mysql -h localhost -u root -p
   ```

3. **Check Database Permissions**
   - Ensure user has CREATE/DROP privileges for migrations
   - Check file permissions for SQLite
   - Verify port is accessible for remote databases

4. **Reinitialize Database**
   ```bash
   # Stop container
   docker stop printfarmer-api
   
   # Remove database (SQLite)
   rm farm.db
   
   # Restart (will recreate database)
   docker start printfarmer-api
   ```

### Slow Queries

**Symptoms:** Dashboard loads slowly, API responses are delayed

**Solutions:**

1. **Check Query Plans**
   - Monitor database query logs
   - Identify slow queries
   - Add indexes if needed

2. **Increase Connection Pool**
   ```csharp
   // In appsettings.json
   "ConnectionStrings": {
     "DefaultConnection": "... ;Max Pool Size=20"
   }
   ```

3. **Enable Query Caching**
   - React Query caching is automatic
   - Verify `staleTime` is appropriate

4. **Denormalize Data**
   - Printer counts already denormalized
   - Add more denormalized fields if needed

## API Issues

### API Server Won't Start

**Symptoms:** Port 5245 shows "connection refused", server won't listen

**Solutions:**

1. **Check Port**
   ```bash
   # See what's using the port
   lsof -ti:5245
   
   # Kill if needed
   kill -9 <PID>
   ```

2. **Check .NET Installation**
   ```bash
   dotnet --info
   # Should show .NET 10.0 SDK
   ```

3. **Review Startup Logs**
   ```bash
   # Run with verbose output
   dotnet run --project ./api/Farm.Web.Api.csproj --verbose
   ```

4. **Check appsettings.json**
   - Verify JSON syntax
   - Check required fields are present
   - Verify URLs are correct

### 401 Unauthorized

**Symptoms:** API calls return 401, login doesn't work

**Solutions:**

1. **Verify JWT Configuration**
   - Check secret key in `appsettings.json`
   - Secret must be consistent across requests

2. **Check Token Expiration**
   ```javascript
   // In browser console
   const token = localStorage.getItem('token');
   console.log(JSON.parse(atob(token.split('.')[1])));
   // Check exp (expiration) claim
   ```

3. **Clear Browser Storage**
   ```javascript
   localStorage.clear();
   sessionStorage.clear();
   // Reload page and log in again
   ```

4. **Restart Auth Services**
   - Log out and log in again
   - Or clear all cookies/tokens

### 500 Server Error

**Symptoms:** API returns 500 error, generic error message

**Solutions:**

1. **Check Server Logs**
   ```bash
   # Docker
   docker logs printfarmer-api | tail -100
   
   # Local
   tail -f ./logs/printfarmer.log
   ```

2. **Enable Debug Logging**
   ```bash
   # Set environment variable
   export SERILOG_LEVEL=Debug
   
   # Restart server
   ```

3. **Check Stack Trace**
   - Search logs for the endpoint being called
   - Look for exception details
   - Check inner exceptions

4. **Validate Request Body**
   - Ensure JSON is valid
   - Check required fields
   - Verify types match expected schema

## Frontend Issues

### React App Won't Load

**Symptoms:** Page shows blank, error in console

**Solutions:**

1. **Check Browser Console**
   - Press F12 to open DevTools
   - Look for JavaScript errors
   - Check Network tab for failed requests

2. **Verify API Connection**
   ```bash
   curl http://localhost:5245/healthz
   ```

3. **Clear Cache**
   - Hard refresh: Ctrl+Shift+R
   - Clear cache: Delete all site data in DevTools
   - Clear localStorage: `localStorage.clear()`

4. **Check Environment Variables**
   ```bash
   # Verify API URL is correct
   echo $VITE_API_BASE_URL
   # Should point to API server
   ```

5. **Rebuild if Needed**
   ```bash
   cd ./src/Web/ReactApp
   npm install
   npm run build
   npm run dev
   ```

### Styles Not Loading

**Symptoms:** Page shows unformatted/broken layout

**Solutions:**

1. **Check Tailwind CSS**
   - Verify `src/index.css` includes the `@theme` block with design tokens
   - Check `src/index.css` imports Tailwind via `@import "tailwindcss"`
   - Rebuild: `npm run build`

2. **Clear Tailwind Cache**
   ```bash
   cd ./src/Web/ReactApp
   rm -rf node_modules/.vite
   npm run dev
   ```

3. **Verify CSS Files**
   ```bash
   # Check dist folder has CSS
   ls -la dist/*.css
   ```

### TypeScript Errors

**Symptoms:** Build fails with TypeScript errors

**Solutions:**

1. **Type Check**
   ```bash
   # Check current errors
   npx tsc --noEmit
   ```

2. **Fix Type Errors**
   - Read error messages carefully
   - Add explicit types where needed
   - Check type definitions are correct

3. **Update Types**
   - Check `src/types/*.ts` files
   - Ensure types match API responses
   - Use `camelCase` for property names

4. **Disable Strict Mode (Temporary)**
   - Edit `tsconfig.json`
   - Set `"strict": false`
   - Fix errors and re-enable

## Docker Issues

### Container Won't Start

**Symptoms:** Container exits immediately after starting

**Solutions:**

1. **Check Logs**
   ```bash
   docker logs printfarmer-api
   docker logs printfarmer-web
   ```

2. **Verify Image**
   ```bash
   # List images
   docker images | grep printfarmer
   
   # Build if missing
   docker build -t printfarmer:latest .
   ```

3. **Check Environment Variables**
   ```bash
   # Verify .env file
   cat .env
   
   # Or check docker-compose.yml for variables
   ```

4. **Check Volumes**
   ```bash
   # Ensure mounted volumes exist
   ls -la <host-path>
   
   # Fix permissions if needed
   chmod 755 <host-path>
   ```

### Port Already in Use

**Symptoms:** "Address already in use" error

**Solutions:**

```bash
# Find process using port
lsof -ti:5245

# Kill process
kill -9 <PID>

# Or change port in docker-compose.yml
# Change: 5245:5245 to 5246:5245
```

### Network Issues in Docker

**Symptoms:** Container can't reach external printers/databases

**Solutions:**

1. **Use Host Network**
   ```bash
   # In docker-compose.yml
   network_mode: "host"
   ```

2. **Check Container Network**
   ```bash
   # Inspect network
   docker network inspect <network-name>
   
   # Test connectivity from container
   docker exec printfarmer-api ping <printer-ip>
   ```

3. **Verify DNS**
   ```bash
   # Test DNS resolution
   docker exec printfarmer-api nslookup <hostname>
   ```

## Performance Issues

### Slow Dashboard Load

**Symptoms:** Dashboard takes > 5 seconds to load

**Solutions:**

1. **Monitor Network**
   - Open DevTools Network tab
   - Check which requests are slow
   - Look for large response sizes

2. **Check Database Queries**
   - Monitor slow query log
   - Identify problematic queries
   - Add indexes if needed

3. **Optimize Frontend**
   - Lazy load routes
   - Memoize components
   - Reduce re-renders

4. **Check Server Resources**
   - Monitor CPU usage
   - Check memory availability
   - Look for disk I/O bottlenecks

### High Memory Usage

**Symptoms:** Docker container using > 1GB RAM

**Solutions:**

1. **Restart Container**
   ```bash
   docker restart printfarmer-api
   ```

2. **Check for Memory Leaks**
   - Monitor memory over time
   - Look for continuously growing memory
   - Check logs for large operations

3. **Limit Container Memory**
   ```yaml
   # In docker-compose.yml
   services:
     api:
       mem_limit: 512m
       mem_reservation: 256m
   ```

4. **Optimize Queries**
   - Implement pagination
   - Reduce loaded data
   - Use streaming for large responses

## Getting Help

### Check Logs

Always check logs first:
```bash
# Docker
docker logs -f printfarmer-api

# Local
tail -f ./logs/printfarmer.log | grep -i error
```

### Debug Information to Provide

When asking for help, provide:
1. Error message (full stack trace)
2. Steps to reproduce
3. Your environment (OS, .NET version, Node version)
4. Recent configuration changes
5. Relevant logs (last 50 lines)

### Report Issues

- [GitHub Issues](https://github.com/OlyForge3D/printfarmer/issues)
- Include error details, logs, and reproduction steps
- Labels help categorize: bug, help wanted, enhancement

### Community Help

- [GitHub Discussions](https://github.com/OlyForge3D/printfarmer/discussions)
- Search for similar questions first
- Provide context and what you've tried
