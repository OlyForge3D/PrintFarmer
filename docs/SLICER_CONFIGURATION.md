# Slicer Configuration for PrintFarmer

This guide explains how to configure popular slicers (PrusaSlicer, OrcaSlicer) to upload G-code files directly to PrintFarmer using the OctoPrint-compatible API.

## Overview

PrintFarmer implements OctoPrint-compatible API endpoints that allow slicers to upload files directly to the print farm management system. Files uploaded through slicers are automatically added to the print queue and require approval before printing begins.

## Supported Slicers

- **PrusaSlicer** (2.0+)
- **OrcaSlicer**
- **SuperSlicer**
- Any slicer that supports OctoPrint integration

## API Endpoints

PrintFarmer provides the following OctoPrint-compatible endpoints required for slicer integration:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/octoprint/version` | GET | Returns server version information (required by slicers for compatibility check) |
| `/api/octoprint/server` | GET | Returns server status |
| `/api/octoprint/files/local` | POST | Uploads a new G-code file with optional auto-dispatch |

These are the minimal endpoints required for PrusaSlicer, OrcaSlicer, and SuperSlicer to upload files to PrintFarmer.

## Configuration Steps

### 1. Generate API Key

Before configuring your slicer, you need to generate an API key in PrintFarmer:

1. Log into PrintFarmer web interface
2. Navigate to **Settings** → **API Keys**
3. Click **Generate New API Key**
4. Give the key a descriptive name (e.g., "PrusaSlicer on Workstation")
5. Copy the generated API key (you won't be able to see it again)

> **Note:** API keys created here default to the **OctoPrint** purpose, which is what slicers need
> for OctoPrint-compatible uploads. A separate **Desktop** purpose exists for the PrintFarmer
> Desktop app; Desktop-purpose keys require explicit scopes and receive a 90-day expiry by
> default, and are never valid for slicer uploads. See
> [Desktop API-Key Exchange](#desktop-api-key-exchange) below for how Desktop-purpose keys are
> exchanged for a short-lived token. Provider migrations and the reserved authentication,
> audit, expiry, scope, and redaction test tranche remain in #839.

### 2. Configure PrusaSlicer

1. Open PrusaSlicer
2. Go to **Configuration** → **Preferences** → **OctoPrint/PrusaLink**
3. Click **Add** to create a new printer connection
4. Fill in the following fields:
   - **Name**: `PrintFarmer` (or any descriptive name)
   - **Hostname/IP**: `your-printfarmer-server.local` or IP address
   - **Port**: `5245` (default PrintFarmer API port)
   - **API Key**: Paste the API key you generated earlier
   - **HTTPS**: Uncheck (unless you've configured HTTPS)
5. Click **Test** to verify the connection
6. Click **OK** to save

### 3. Configure OrcaSlicer

1. Open OrcaSlicer
2. Go to **Preferences** → **Network**
3. In the **OctoPrint** section, click **Add**
4. Fill in the following fields:
   - **Printer Name**: `PrintFarmer`
   - **Host**: `http://your-printfarmer-server.local:5245` or `http://IP:5245`
   - **API Key**: Paste the API key you generated earlier
5. Click **Test** to verify the connection
6. Click **OK** to save

### 4. Configure SuperSlicer

Configuration is identical to PrusaSlicer:

1. Go to **Configuration** → **Preferences** → **Physical Printers**
2. Add a new OctoPrint printer with PrintFarmer details
3. Test and save the connection

## Usage

### Uploading Files

After configuring your slicer:

1. Slice your 3D model as usual
2. Instead of **Export G-code**, click **Send to OctoPrint** (or similar option depending on slicer)
3. Select your PrintFarmer connection
4. Optionally check **Start print after upload** (file will be queued for approval)
5. Click **Send**

The file will be uploaded to PrintFarmer and appear in the G-code library.

### Print Approval Workflow

When you upload a file with "Start print after upload" enabled:

1. File is uploaded to PrintFarmer
2. A print job is created in **Pending Approval** state
3. Navigate to **Print Approvals** in PrintFarmer web interface
4. Review the uploaded file details
5. Click **Approve** to add it to the print queue
6. Optionally assign a specific printer when approving
7. The job will be scheduled for printing once a printer becomes available

Without "Start print after upload", files are simply added to the library and can be queued manually later.

## Troubleshooting

### Connection Test Fails

**Error**: "Could not connect to OctoPrint"

**Solutions**:
- Verify PrintFarmer API server is running: `curl http://your-server:5245/api/octoprint/version`
- Check firewall rules allow connections to port 5245
- Ensure you're using `http://` not `https://` unless HTTPS is configured
- Verify the API key is correct and hasn't been revoked

### Upload Fails

**Error**: "Unauthorized" or "Invalid API key"

**Solutions**:
- Regenerate your API key and update slicer configuration
- Check that the API key hasn't expired or been deleted
- Verify the API key has upload permissions

### Files Not Appearing

**Solutions**:
- Check the **G-code Library** in PrintFarmer web interface
- Verify you have sufficient storage quota
- Check server logs for upload errors: `docker logs printfarmer-api`

### Rate Limiting

If you're uploading many files quickly, you may hit rate limits.

**Error**: "Rate limit exceeded" (HTTP 429)

**Solutions**:
- Wait 1 minute before retrying
- Adjust rate limits in PrintFarmer settings (admin only)
- Reduce concurrent uploads

## API Reference

### Upload Endpoint

```http
POST /api/octoprint/files/local?print=true&printerId=<guid>
Headers:
  X-Api-Key: your-api-key-here
  Content-Type: multipart/form-data
Body:
  file: <binary G-code file>
```

**Parameters**:
- `print` (optional, default: false) - If true, creates a print job immediately
- `printerId` (optional) - Specific printer GUID to assign the job to

**Response** (with print=true):
```json
{
  "file": {
    "fileName": "model.gcode",
    "fileSize": 1234567,
    "gcodeFileId": "guid-here"
  },
  "jobId": "job-guid",
  "approvalId": "approval-guid",
  "status": "PendingApproval"
}
```

**Note**: File management (listing, deleting) should be done through the PrintFarmer web interface, not through the OctoPrint API.

## Security Notes

- **API keys are sensitive**: Treat them like passwords. Don't share or commit them to version control.
- **Use HTTPS in production**: Configure HTTPS for PrintFarmer in production environments to encrypt API keys in transit.
- **Rate limiting**: PrintFarmer enforces rate limits to prevent abuse. Default is 60 uploads per minute per API key.
- **Audit logging**: All uploads via API are logged with API key identifier for traceability.

## Advanced Configuration

### Custom Ports

If PrintFarmer is running on a non-standard port, update the hostname/IP in your slicer configuration:
- Instead of `your-server.local`, use `your-server.local:8080`
- Ensure the port number matches your PrintFarmer deployment

### Reverse Proxy Setup

When using a reverse proxy (Nginx, Traefik):
- Configure the proxy to forward `/api/octoprint/*` to the PrintFarmer API server
- Ensure WebSocket support is enabled for SignalR
- Set appropriate timeouts for large file uploads
- Update slicer configuration to use the proxy hostname/port

### Multiple Printer Farms

To manage multiple PrintFarmer instances:
1. Generate separate API keys for each farm
2. Create separate printer connections in your slicer for each farm
3. Give each connection a descriptive name (e.g., "PrintFarmer - Office", "PrintFarmer - Workshop")
4. Select the appropriate connection when uploading

## See Also

- [API Documentation](API.md) - Complete PrintFarmer API reference
- [Print Approval Workflow](ARCHITECTURE.md#print-approval-workflow) - Architecture details
- [OctoPrint API Specification](https://docs.octoprint.org/en/master/api/) - Original OctoPrint API

## Desktop API-Key Exchange

The PrintFarmer Desktop app authenticates with a **Desktop**-purpose API key (see the note under
[Generate API Key](#1-generate-api-key)), but never sends that key on every request. Instead it
exchanges the key once for a short-lived JWT, which is what the main API and the standalone
slicer host both accept for model/library requests.

### Exchange Endpoint

```http
POST /api/auth/api-key/exchange
Content-Type: application/json

{
  "apiKey": "your-desktop-api-key-here"
}
```

**Response** (200 OK):

```json
{
  "token": "eyJhbGciOi...",
  "expiresAt": "2026-01-20T12:15:00Z",
  "scopes": ["ModelRead", "ModelWrite"]
}
```

**Response** (401 Unauthorized) - returned uniformly for a missing, malformed, unknown, revoked,
expired, wrong-purpose (non-Desktop), or under-scoped (no scopes granted) key, and never reveals
which of these applies:

```json
{
  "error": "Invalid API key"
}
```

The endpoint is rate-limited per client IP address (`RateLimiting:Authentication:MaxApiKeyExchangeAttemptsPerMinute`,
default 5/minute) to resist brute-force and enumeration attempts, and every exchange attempt
(success or failure) is recorded in the authentication audit log. The raw API key, its hash, and
the issued JWT are never written to logs or audit records.

### Token Claims and Lifetime

The issued token is a normal JWT signed with the same `Jwt:Key`/`Jwt:Issuer`/`Jwt:Audience`
configuration as login tokens, so it validates identically wherever those settings match, but it
carries a deliberately minimal claim set: the owning user's identity, a `token_use=desktop_exchange`
marker, the API key's ID, and one `scope` claim per scope granted to the exchanged key
(`ModelRead`, `ModelWrite`, `LibrarySync`) - no role or permission claims. Its lifetime is
configurable via `Jwt:DesktopExchangeLifetimeMinutes` (default 15 minutes) and is independent of,
and much shorter than, the API key's own 90-day expiry.

### Authorization Policies

`ModelRead`, `ModelWrite`, and `LibrarySync` authorization policies gate the corresponding
model/library endpoints on both the main API (`GcodeLibraryController`) and the slicer host
(`Model3DFilesController`). A normal login/session token is unaffected by these policies (they
only constrain principals carrying the `token_use=desktop_exchange` claim), and legacy/unscoped
**OctoPrint**-purpose keys can never obtain a Desktop-exchange token, since the exchange endpoint
rejects any key whose `Purpose` is not `Desktop`.

### Slicer Host Configuration

To accept Desktop-exchanged tokens, the standalone slicer host needs the same JWT signing
configuration as the main API - see `Jwt__Key`, `Jwt__Issuer`, and `Jwt__Audience` in
`scripts/docker/compose-templates/docker-compose.slicer-host.yml`. If `Jwt__Key` is not configured
(or shorter than 32 characters), the slicer host runs in its existing standalone mode: no JWT
Bearer scheme is registered and every request authenticates as an admin, exactly as before this
feature was added.

**Security note:** unlike the main API (which refuses to start without an explicit `Jwt__Key`),
the slicer-host compose template deliberately ships with `Jwt__Key` **unset by default** so
standalone-only deployments keep working with zero JWT configuration. Never set `Jwt__Key` in
either service to the well-known placeholder value that appears anywhere in this repository's
committed example configuration - a shared/known signing key lets anyone mint their own valid
`ModelWrite`/`LibrarySync`-scoped tokens. Always generate a real random secret (e.g.
`openssl rand -base64 48`) and set the identical value for both the `api` and `slicer-host`
services via environment variables or a secrets manager, never by committing it to source
control.

## Support

For issues or questions:
- Check PrintFarmer logs: `docker logs printfarmer-api`
- Open an issue on GitHub
- Check existing documentation in the `docs/` folder
