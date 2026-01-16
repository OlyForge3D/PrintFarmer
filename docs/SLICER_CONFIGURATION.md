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
| `/api/octoprint/files/local` | POST | Uploads a new G-code file with optional auto-print |

These are the minimal endpoints required for PrusaSlicer, OrcaSlicer, and SuperSlicer to upload files to PrintFarmer.

## Configuration Steps

### 1. Generate API Key

Before configuring your slicer, you need to generate an API key in PrintFarmer:

1. Log into PrintFarmer web interface
2. Navigate to **Settings** → **API Keys**
3. Click **Generate New API Key**
4. Give the key a descriptive name (e.g., "PrusaSlicer on Workstation")
5. Copy the generated API key (you won't be able to see it again)

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

## Support

For issues or questions:
- Check PrintFarmer logs: `docker logs printfarmer-api`
- Open an issue on GitHub
- Check existing documentation in the `docs/` folder
