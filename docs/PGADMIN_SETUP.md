# pgAdmin Support for PrintFarmer PostgreSQL Deployments

## Overview

PrintFarmer now includes optional pgAdmin 4 support for PostgreSQL deployments. pgAdmin is a web-based administration tool for PostgreSQL that helps with database debugging, query execution, and database management.

## Features

- **Automatic Configuration**: pgAdmin is automatically configured to connect to the PrintFarmer PostgreSQL database
- **Persistent Across Teardowns**: pgAdmin container is preserved during teardown operations for debugging
- **Conditional Deployment**: pgAdmin only deploys when:
  - `--enable-pgadmin` flag is used
  - PostgreSQL is the selected database provider
  - Container isn't already deployed (idempotent)
- **Web Interface**: Accessible on port 5050 at `http://localhost:5050/pgadmin`

## Usage

### Enable pgAdmin During Deployment

```bash
./scripts/deploy-docker.sh --architecture microservices --enable-pgadmin
```

Or in non-interactive mode:

```bash
./scripts/deploy-docker.sh --architecture microservices --enable-pgadmin --non-interactive
```

### Default Credentials

- **Email**: `admin@printfarmer.local`
- **Password**: `adminpass`

**Security Note**: These are default credentials for development. Change them in production by modifying the `PGADMIN_DEFAULT_EMAIL` and `PGADMIN_DEFAULT_PASSWORD` environment variables in `.env` file before deployment.

### Access pgAdmin

After deployment, access pgAdmin at:

```
http://localhost:5050/pgadmin
```

Or from a remote machine:

```
http://<your-server-ip>:5050/pgadmin
```

## How It Works

### Compose Generation

The `compose-generator.sh` script detects the `--enable-pgadmin` flag and:
1. Verifies PostgreSQL is the database provider
2. Merges the pgAdmin service configuration from `docker-compose.pgadmin.yml`
3. Applies automatic server configuration from `pgadmin-init.json`

### Auto-Configuration

pgAdmin is configured with automatic server connection via:
- **Host**: The `database` service (or `postgres` hostname)
- **Port**: `5432`
- **Database**: `printfarmer` (configurable via `POSTGRES_DB`)
- **Username**: `postgres` (configurable via `POSTGRES_USER`)
- **Password**: From `POSTGRES_PASSWORD` environment variable

### Teardown Behavior

During teardown operations:
- pgAdmin container is **NOT removed** (intentionally preserved for debugging)
- All other services are stopped and removed as normal
- To fully remove pgAdmin: `docker rm -f printfarmer-pgadmin`

### Health Checks

pgAdmin includes health monitoring:
- Checks endpoint: `http://localhost:80/pgadmin4/misc/ping`
- Interval: 10 seconds
- Timeout: 5 seconds
- Retries: 5 before considered unhealthy

## Troubleshooting

### pgAdmin Not Accessible

1. Check if container is running:
   ```bash
   docker ps | grep pgadmin
   ```

2. View logs:
   ```bash
   docker logs printfarmer-pgadmin
   ```

3. Test connection manually:
   ```bash
   docker exec printfarmer-pgadmin wget --spider -q http://localhost:80/pgadmin4/misc/ping
   ```

### Cannot Connect to Database from pgAdmin

- Verify database container is running: `docker ps | grep database`
- Check network connectivity: `docker exec printfarmer-pgadmin ping database`
- Verify credentials in `POSTGRES_PASSWORD` environment variable in `.env`
- Check PostgreSQL logs: `docker logs printfarmer-database-postgres`

### Port 5050 Already in Use

If port 5050 is already in use, modify the port mapping in `.env`:

```bash
PGADMIN_PORT=5051  # Use a different port
```

Then redeploy:

```bash
./scripts/deploy-docker.sh --redeploy
```

## Configuration

Edit `.env` file to customize pgAdmin:

```bash
# pgAdmin configuration
PGADMIN_IMAGE=dpage/pgadmin4:latest
PGADMIN_DEFAULT_EMAIL=admin@printfarmer.local
PGADMIN_DEFAULT_PASSWORD=yourSecurePassword
PGADMIN_PORT=5050
EXTERNAL_PGADMIN_PATH=.volumes/printfarmer-pgadmin  # Data persistence
```

## Database Compatibility

pgAdmin is **only supported with PostgreSQL**. Other database providers will skip pgAdmin deployment with a warning:

- ✅ PostgreSQL / PostgreSQL
- ❌ SQLite (unsupported)
- ❌ SQL Server (unsupported)
- ❌ MySQL (unsupported)

## Files Modified

### New Files

- `scripts/docker/compose-templates/docker-compose.pgadmin.yml` - pgAdmin service configuration
- `scripts/docker/pgadmin-init.json` - Auto-configuration for database connection

### Modified Files

- `scripts/deploy-docker.sh` - Added `--enable-pgadmin` flag and deployment logic
- `scripts/docker/compose-generator.sh` - Added pgAdmin service merging

## Examples

### Development Deployment with pgAdmin and Monitoring

```bash
./scripts/deploy-docker.sh \
  --architecture microservices \
  --include-monitoring \
  --enable-pgadmin \
  --non-interactive
```

### Upgrade Existing Deployment to Include pgAdmin

```bash
# If you have an existing PostgreSQL deployment:
./scripts/deploy-docker.sh --enable-pgadmin
```

The script will:
1. Regenerate compose with pgAdmin enabled
2. Check if pgAdmin is already deployed
3. If not, deploy it automatically
4. If already running, skip deployment

### Manual pgAdmin Management

```bash
# View pgAdmin logs
docker compose logs -f pgadmin

# Access container shell
docker exec -it printfarmer-pgadmin sh

# Restart pgAdmin
docker compose restart pgadmin

# Stop pgAdmin (preserve data)
docker compose stop pgadmin

# Remove pgAdmin (deletes container and data)
docker rm -f printfarmer-pgadmin
docker volume rm printfarmer_pgadmin-volume  # If volume exists
```

## Best Practices

1. **Change Default Password**: Always change the default credentials in production
2. **Network Isolation**: Use firewall rules to restrict access to pgAdmin in production
3. **Backup Configuration**: pgAdmin data is stored in `.volumes/printfarmer-pgadmin/` (volume name: configurable)
4. **Regular Updates**: Keep pgAdmin image updated: `docker pull dpage/pgadmin4:latest`
5. **HTTPS in Production**: Consider using a reverse proxy with HTTPS/SSL in production

## Performance Considerations

- pgAdmin uses ~200-300MB RAM when idle
- Database queries from pgAdmin may impact performance during heavy loads
- Consider disabling pgAdmin in production if not needed

## Additional Resources

- [pgAdmin Documentation](https://www.pgadmin.org/docs/)
- [PostgreSQL Official Documentation](https://www.postgresql.org/docs/)
- [PrintFarmer Docker Deployment Guide](DOCKER_DEPLOYMENT.md)
