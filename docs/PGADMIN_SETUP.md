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
3. Generates a server configuration file (`pgadmin-servers.json`) with database connection details

### Server Configuration

During deployment, a `pgadmin-servers.json` file is automatically generated with:
- **Host**: The `database` service (or `postgres` hostname)
- **Port**: `5432`
- **Database**: `printfarmer` (configurable via `POSTGRES_DB`)
- **Username**: `postgres` (configurable via `POSTGRES_USER`)
- **Note**: Password must be entered manually (pgAdmin security requirement)

This file is mounted into the pgAdmin container, but **pgAdmin does not automatically import it**. Instead, it provides a ready-to-import template for manual server registration.

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

## Importing the PostgreSQL Server

PrintFarmer automatically generates a pre-configured `servers.json` file (in `.volumes/printfarmer-pgadmin/servers.json`) with all connection details during deployment. However, pgAdmin does not automatically import this file, so you must import it manually through the web interface.

### Manual Server Import via pgAdmin UI

1. **Login to pgAdmin**
   - Open: `http://localhost:5050/pgadmin`
   - Email: `admin@printfarmer.local`
   - Password: `adminpass`

2. **Navigate to Import**
   - In the top menu: **Tools** → **Import/Export** → **Import Servers**

3. **Select the Servers File**
   - The file is located at: `.volumes/printfarmer-pgadmin/servers.json`
   - Click **Browse** and select the file
   - Click **Import**

4. **Enter the Database Password**
   - When prompted, enter the PostgreSQL password from your `.env` file (`POSTGRES_PASSWORD`)
   - Click **Finish**

5. **Verify Connection**
   - In the left sidebar, expand **Servers** → **PrintFarmer PostgreSQL**
   - You should see the database structure
   - If connection fails, check troubleshooting section below

### Command-Line Server Import (Advanced)

If you prefer command-line automation, you can use pgAdmin's setup.py:

```bash
docker exec printfarmer-pgadmin python /pgadmin4/setup.py load-servers /pgadmin4/servers.json
```

**Note**: This requires the pgAdmin container to have all necessary Python dependencies installed.

## Server Configuration File Format

The `servers.json` file is automatically generated in `.volumes/printfarmer-pgadmin/servers.json` during deployment and contains the pgAdmin JSON import format. Understanding this format is helpful if you need to:
- Edit the file manually
- Create additional server configurations
- Understand what will be imported

### File Location

- **On host machine**: `.volumes/printfarmer-pgadmin/servers.json`
- **In container**: `/var/lib/pgadmin/servers.json`

### JSON Structure

```json
{
    "Servers": {
        "1": {
            "Name": "PrintFarmer PostgreSQL",
            "Group": "Servers",
            "Port": 5432,
            "Username": "postgres",
            "Host": "database",
            "MaintenanceDB": "postgres",
            "ConnectionParameters": {
                "sslmode": "prefer"
            },
            "Comment": "PrintFarmer database server - password must be entered manually on first connection"
        }
    }
}
```

### Field Descriptions

| Field | Description | Example |
|-------|-------------|---------|
| `Name` | Display name for the server in pgAdmin | `PrintFarmer PostgreSQL` |
| `Group` | Server group for organization | `Servers`, `Development`, etc. |
| `Host` | PostgreSQL hostname or IP address | `database`, `localhost`, `192.168.1.100` |
| `Port` | PostgreSQL port number | `5432` |
| `Username` | PostgreSQL user for connection | `postgres`, `app_user` |
| `MaintenanceDB` | Database to use for maintenance operations | `postgres` (required) |
| `ConnectionParameters` | Advanced connection settings | See below |
| `Comment` | Optional notes about the server | Any text |

### ConnectionParameters Options

```json
"ConnectionParameters": {
    "sslmode": "prefer",           // prefer, require, disable
    "connect_timeout": 10,         // seconds
    "application_name": "pgadmin"  // optional
}
```

### Important Notes

1. **Password Field**: pgAdmin **does not support** importing passwords for security reasons. Passwords must be entered manually through the UI when first connecting.

2. **SSL Mode Options**:
   - `prefer`: Try SSL first, fall back to non-SSL
   - `require`: Always use SSL
   - `disable`: Never use SSL

3. **Multiple Servers**: To add multiple servers, create additional entries in the `Servers` object with unique keys:
   ```json
   {
       "Servers": {
           "1": { /* first server */ },
           "2": { /* second server */ },
           "3": { /* third server */ }
       }
   }
   ```

### Reference

See [pgAdmin Import/Export Documentation](https://www.pgadmin.org/docs/pgadmin4/latest/import_export_servers.html) for complete format specification.

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
