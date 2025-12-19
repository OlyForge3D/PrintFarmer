# CSV Import Format - Detailed Reference

## File Format

- **Extension**: `.csv` (comma-separated values)
- **Encoding**: UTF-8
- **Delimiter**: Comma (`,`)
- **Header**: First row must contain column names (case-insensitive)

## Required Fields

These fields must be present in every row:

| Field | Type | Example | Notes |
|-------|------|---------|-------|
| `name` | string | "Prusa MK4" | Unique printer name (max 100 chars) |
| `ipaddress` | string | "192.168.1.100" | IP address of printer (hostname supported via DNS) |
| `backend` | enum | "Moonraker" | Backend type: Moonraker, PrusaLink, SDCP |

## Optional Fields

| Field | Type | Example | Default | Notes |
|-------|------|---------|---------|-------|
| `manufacturername` | string | "Prusa" | "Unknown" | Manufacturer name (portable between systems) |
| `modelname` | string | "MK4" | "Unknown" | Model name (portable between systems) |
| `notes` | string | "Office printer" | null | Admin notes (max 1000 chars) |
| `isenabled` | boolean | true/false | true | Controls visibility to users; false = pending approval |
| `backendport` | integer | 7125 | null | Port for printer API |
| `frontendport` | integer | 80 | null | Port for web UI |
| `camerastreamurl` | string | "rtsp://192.168.1.100/stream" | null | Camera stream URL |
| `camerasnapshoturl` | string | "http://192.168.1.100/snapshot" | null | Camera snapshot URL |
| `dateacquired` | date | "2024-01-15" | null | Purchase date (yyyy-MM-dd format) |

## Important Notes on IDs vs Names

⚠️ **DO NOT use ManufacturerId or ModelId in CSV files** - These are auto-generated database IDs that are not portable between systems!

- **Instead of IDs**: Use `manufacturername` and `modelname`
- **Automatic lookup**: The import process finds or creates manufacturers/models by name
- **Benefit**: CSVs can be shared between different PrintFarmer instances
- **On import**: If manufacturer/model doesn't exist, it's created automatically

## Special Handling

### String Fields
- Quoted values: `"value with, comma"` → parsed as `value with, comma`
- Quote escaping: `"value with "" quote"` → parsed as `value with " quote`
- Empty values: Empty string or omitted → null

### Enum Fields (backend)
- Case-insensitive: `MOONRAKER`, `moonraker`, `Moonraker` all work
- Valid values: `Moonraker`, `PrusaLink`, `SDCP`
- Invalid values: **ERROR - field is required!**

### IP Address
- Format: IPv4 address (e.g., `192.168.1.100`) or hostname (e.g., `printer.local`)
- **Required**: Must be present in every row
- Hostname resolution: Performed automatically via DNS during printer creation

### String Fields (manufacturerName, modelName)
- **Optional**: Can be left empty or omitted
- **Automatic creation**: If name is provided and doesn't exist, it's created
- **Case-sensitive**: Different cases = different entries (use consistently)
- **Portable**: Use human-readable names, not database IDs

### Integer Fields (ports)
- Must be valid integers: `7125`, `80`, `443`
- Invalid values: Skipped (treated as null)
- Valid range: 1-65535
- Port selection: If omitted, defaults based on backend type

### Boolean Fields (isEnabled)
- Case-insensitive: `true`, `True`, `TRUE` all parsed as `true`
- Aliases: `1`, `yes`, `y` → `true`
- Aliases: `0`, `no`, `n` → `false`
- Default: `true` if not present
- Workflow: Discover sets `isEnabled=false` (pending approval), admin edits to `true` to approve

### Date Fields (dateAcquired)
- Format: `yyyy-MM-dd` (e.g., `2024-01-15`)
- Optional: Leave blank or omit
- Invalid dates: Skipped

## Examples

### Minimal CSV (Only Required Fields)
```csv
name,ipaddress,backend
"Office Prusa","192.168.1.100","Moonraker"
"Shop Ender3","192.168.1.101","Moonraker"
```

### Full CSV with Optional Metadata
```csv
name,ipaddress,backend,manufacturername,modelname,notes,isenabled
"Prusa MK4","192.168.1.100","Moonraker","Prusa","MK4","Main office printer",true
"Ender3 Pro","192.168.1.101","Moonraker","Creality","Ender3 Pro","Backup printer",false
"PrusaLink Mini","192.168.1.102","PrusaLink","Prusa","Mini","Remote site - pending approval",false
```

### Discovery Export Format
CSV exported from `AdminCli --discover --format csv`:
```csv
Name,IpAddress,Backend,ManufacturerName,ModelName,Notes,IsEnabled
"Moonraker-192.168.1.100","192.168.1.100","Moonraker","Unknown","Unknown","Auto-discovered",false
"PrusaLink-192.168.1.101","192.168.1.101","PrusaLink","Unknown","Unknown","Auto-discovered",false
"SDCP-192.168.1.102","192.168.1.102","SDCP","Unknown","Unknown","Auto-discovered",false
```

## AdminCli Command Line Arguments

### Discovery Commands

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `--discover` | flag | - | Execute network discovery scan |
| `--range <ranges>` | string | Auto-detect | CIDR ranges to scan (comma-separated: `192.168.1.0/24,10.0.0.0/24`) |
| `--interface <names>` | string | All active | Specific network interfaces to use (comma-separated: `en0,eth0`) |
| `--timeout <ms>` | integer | 200 | Probe timeout in milliseconds (e.g., `500` for 500ms) |
| `--concurrent <count>` | integer | 10 | Max concurrent probe operations (e.g., `50` for faster scanning) |
| `--format <json\|csv>` | enum | json | Output format: `json` or `csv` |
| `--output <file>` | string | stdout | Save output to file (e.g., `discovered.csv`) |
| `--no-approval` | flag | - | Set discovered printers to `isEnabled=true` (skip approval workflow) |

### CSV Template Commands

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `--sample-csv` | flag | - | Generate sample CSV template with examples |
| `--output <file>` | string | stdout | Save sample CSV to file |

### Admin Setup Commands

| Argument | Type | Default | Description |
|----------|------|---------|-------------|
| `--status` | flag | - | Show if initial setup is required |
| `--username <value>` | string | - | Admin username for setup |
| `--email <value>` | string | - | Admin email for setup |
| `--password <value>` | string | - | Admin password (minimum 12 characters) |
| `--first-name <value>` | string | - | Admin first name (optional) |
| `--last-name <value>` | string | - | Admin last name (optional) |
| `--base-url <url>` | string | http://localhost:5245 | API base URL for connections |

### Special Notes

- **Range format**: CIDR notation required (e.g., `10.0.0.0/24`, `192.168.1.0/16`)
- **Interface names**: OS-specific (macOS: `en0`, Linux: `eth0`, Windows: `Ethernet`)
- **Auto-detect ranges**: If `--range` not specified, discovery uses saved config or detects local networks
- **Saved config**: Discovery settings are cached from previous runs
- **Concurrent probes**: Higher values = faster scanning but more network load (default 10, max recommended ~50)

## Workflow: From Discovery to Import

### 1. Generate Sample CSV
```bash
dotnet run --project src/tools/AdminCli -- --sample-csv --output sample.csv
```

Shows examples for each backend type (Moonraker, PrusaLink, SDCP).

### 2. Discover Printers

**Basic discovery (uses auto-detected networks and saved config):**
```bash
dotnet run --project src/tools/AdminCli -- --discover --format csv --output discovered.csv
```

**Specific network range:**
```bash
dotnet run --project src/tools/AdminCli -- --discover --range 192.168.1.0/24 --format csv --output discovered.csv
```

**Multiple ranges with faster scanning:**
```bash
dotnet run --project src/tools/AdminCli -- --discover --range '192.168.1.0/24,10.0.0.0/8' --timeout 500 --concurrent 50 --format csv --output discovered.csv
```

**Specific network interface:**
```bash
dotnet run --project src/tools/AdminCli -- --discover --interface en0 --format csv --output discovered.csv
```

**Skip approval workflow (auto-enable discovered printers):**
```bash
dotnet run --project src/tools/AdminCli -- --discover --range 192.168.1.0/24 --no-approval --format csv --output discovered.csv
```

Output includes auto-discovered printers with `isEnabled=false` (or `true` if `--no-approval` used):
```csv
Name,IpAddress,Backend,ManufacturerName,ModelName,Notes,IsEnabled
"Moonraker-192.168.1.100","192.168.1.100","Moonraker","Unknown","Unknown","Auto-discovered",false
```

### 3. Admin Reviews & Edits
1. Open `discovered.csv` in spreadsheet editor
2. Update `ManufacturerName` and `ModelName` based on actual printers
3. Set `IsEnabled` to `true` for approved printers, leave `false` for pending review
4. Keep `IpAddress` and `Backend` as discovered
5. Add optional `Notes` for admin reference

Example after editing:
```csv
Name,IpAddress,Backend,ManufacturerName,ModelName,Notes,IsEnabled
"Production Prusa","192.168.1.100","Moonraker","Prusa","MK4","Main production",true
"Testing Ender3","192.168.1.101","Moonraker","Creality","Ender3 Pro","Backup device",false
"Remote Mini","192.168.1.102","PrusaLink","Prusa","Mini","Branch office",false
```

### 4. Import to API
```bash
curl -X POST http://localhost:5245/api/printers/import \
  -F "file=@reviewed_printers.csv"
```

Response:
```json
{
  "importedCount": 2,
  "skippedCount": 0,
  "failureCount": 1,
  "results": [...],
  "errors": ["Row 3: Invalid backend specified"]
}
```

### 5. Enable Approved Printers
After import and verification:
1. Printers with `isEnabled=true` immediately appear in dashboards
2. Printers with `isEnabled=false` are hidden (pending admin approval via UI)
3. Admin can toggle `isEnabled` in UI to approve/hide printers

## Duplicate Handling

When importing, specify how to handle duplicate printers (by name and IP):

```bash
# Skip duplicates (default)
POST /api/printers/import?duplicateHandling=skip

# Overwrite duplicates
POST /api/printers/import?duplicateHandling=overwrite

# Error on duplicates
POST /api/printers/import?duplicateHandling=error
```

## Error Handling

### Parsing Errors
- **Line too short**: Skipped with warning
- **Invalid IP/hostname**: Row rejected
- **Invalid backend enum**: Row rejected (required field)
- **Invalid integer**: Field treated as null (optional)
- **Invalid date**: Field treated as null (optional)
- **Invalid boolean**: Defaults to true (optional)

### Validation Errors
- **Missing required field**: Printer row skipped
- **Invalid IP address**: Row rejected
- **Missing backend**: Row rejected
- **Duplicate name+IP**: Handled per `duplicateHandling` parameter

### Response
```json
{
  "importedCount": 5,
  "skippedCount": 1,
  "failureCount": 2,
  "results": [
    {
      "index": 0,
      "name": "Printer1",
      "status": "Success",
      "id": "guid-here"
    },
    {
      "index": 1,
      "name": "Printer2",
      "status": "Failed",
      "reason": "Invalid IP address format"
    }
  ],
  "errors": ["Row 2: ...", "Row 5: ..."]
}
```

## Validation Rules

### name
- **Length**: 1-100 characters
- **Uniqueness**: Checked with ipAddress (same name on different IPs = OK)
- **Characters**: Alphanumeric, spaces, hyphens, underscores, periods allowed
- **Required**: Yes

### ipaddress
- **Format**: Valid IPv4 address or resolvable hostname
- **Examples**: 
  - ✓ `192.168.1.100`
  - ✓ `printer.local`
  - ✓ `myprinter.example.com`
  - ✗ `192.168.1` (incomplete)
  - ✗ `999.999.999.999` (invalid IP)
- **Required**: Yes
- **Resolution**: Hostname → IP performed automatically

### backend
- **Valid values**: `Moonraker`, `PrusaLink`, `SDCP`
- **Case-insensitive**: `moonraker` → `Moonraker`
- **Invalid**: ERROR (required field!)
- **Required**: Yes

### manufacturerName / modelName
- **Optional**: Can be left empty
- **Auto-creation**: If provided, creates if doesn't exist
- **Portable**: Human-readable names work across systems
- **Examples**: `Prusa`, `Creality`, `Bambu Lab`, `MK4`, `Ender3 Pro`

### notes
- **Optional**: Any text
- **Length**: 0-1000 characters
- **Format**: Free text, newlines allowed (will be normalized)

### isenabled
- **Values**: `true`, `false`, `1`, `0`, `yes`, `no`, `y`, `n` (case-insensitive)
- **Default**: `true` if not provided
- **Purpose**: Control visibility and approval workflow
- **Common pattern**: 
  - Discovery exports with `false` (pending review)
  - Admin edits to `true` (approve)
  - Import with approved/rejected flags

### ports (backendPort, frontendPort)
- **Range**: 1-65535
- **Optional**: Null/empty is valid
- **Type**: Integer only
- **Defaults if omitted**: 
  - Moonraker: 7125 (backend), 80 (frontend)
  - PrusaLink: 80 (backend), 443 (frontend)
  - SDCP: 80 (backend), 80 (frontend)

### dateAcquired
- **Format**: `yyyy-MM-dd`
- **Optional**: Completely optional
- **Examples**: `2024-01-15`, `2023-12-25`
- **Invalid dates**: Skipped with warning

## Tips

1. **Use quotes liberally**: Quote all string fields to avoid parsing issues
2. **Use Excel or LibreOffice**: Edit CSVs in spreadsheet apps for better UX
3. **Export sample first**: Use `AdminCli --sample-csv` to see expected format
4. **Validate before import**: Check column names match requirements
5. **Test with small batch**: Import 1-2 printers first to verify workflow
6. **Keep backups**: Save original discovery export before editing
7. **Use discoverable names**: Help admins identify printers in logs and dashboards
8. **Note approval status**: Add notes about approval pending/approved in CSV

## Discovery Command Examples

### Quick Discovery (Default Settings)
Use saved config from previous runs:
```bash
dotnet run --project src/tools/AdminCli -- --discover
```

### Home Network (Single Subnet)
Scan a typical home network with 2-second timeout:
```bash
dotnet run --project src/tools/AdminCli -- --discover --range 192.168.1.0/24 --timeout 2000 --concurrent 20 --format csv --output home_printers.csv
```

### Small Office (Multiple Subnets)
Scan multiple office networks with aggressive concurrent scanning:
```bash
dotnet run --project src/tools/AdminCli -- --discover --range '192.168.1.0/24,10.0.0.0/24' --timeout 500 --concurrent 50 --format csv --output office_printers.csv
```

### Large Enterprise Network
Scan large IP range with conservative settings:
```bash
dotnet run --project src/tools/AdminCli -- --discover --range 10.0.0.0/8 --timeout 1000 --concurrent 30 --format csv --output enterprise_printers.csv
```

### Specific Interface (Multi-NIC Systems)
Scan only a specific network interface (useful on systems with multiple NICs):
```bash
dotnet run --project src/tools/AdminCli -- --discover --interface en0 --format csv --output en0_printers.csv
```

### Fast Discovery with Auto-Enable
Quick scan with automatic approval (skip review workflow):
```bash
dotnet run --project src/tools/AdminCli -- --discover --range 192.168.1.0/24 --timeout 200 --concurrent 50 --no-approval --format csv --output approved_printers.csv
```

## Performance Tuning

### Timeout Settings
- **Fast networks** (local gigabit): `--timeout 200` (200ms)
- **Standard networks**: `--timeout 500` (500ms)
- **Slow/wireless networks**: `--timeout 1000-2000` (1-2 seconds)
- **Very slow networks**: `--timeout 3000+` (3+ seconds)

### Concurrent Probe Settings
- **Conservative** (minimal load): `--concurrent 5-10`
- **Standard**: `--concurrent 20-30`
- **Aggressive** (fast scanning): `--concurrent 50+`
- **Limits**: Exceeding 100 may cause network issues; monitor system resources

### Scanning Speed Estimates
| Range | Concurrent | Timeout | Est. Time |
|-------|-----------|---------|----------|
| /24 (256 IPs) | 10 | 200ms | 5-10 sec |
| /24 (256 IPs) | 50 | 200ms | 1-2 sec |
| /16 (65k IPs) | 30 | 500ms | 10-15 min |
| /16 (65k IPs) | 50 | 500ms | 5-10 min |
| /8 (16M IPs) | 30 | 1000ms | hours (not recommended) |

## Common Issues

### "CSV must have required columns: 'Name', 'IpAddress', 'Backend'"
- Check column names are exactly `name`, `ipaddress`, and `backend` (case-insensitive)
- Verify header row is the first line
- Check for hidden/extra spaces

### "Invalid backend 'Moonraker-192.168.1.100' at line 2"
- Backend column contains wrong data
- Check column order - `Backend` column may be misaligned
- Verify backend value is one of: Moonraker, PrusaLink, SDCP

### "No valid printer entries found in file"
- All rows were skipped due to validation errors
- Check for required fields (name, ipaddress, backend)
- Check all data types are correct
- Review error details in response

### "Duplicate printer: X already exists"
- Printer with same name and IP exists
- Use `duplicateHandling=overwrite` to replace
- Or change name/IP for new import

### "Invalid IP address format"
- IP address is malformed
- Check format: `xxx.xxx.xxx.xxx` or valid hostname
- Leading/trailing spaces are allowed and trimmed

---

## AdminCli Help and Setup

### Display Help
```bash
dotnet run --project src/tools/AdminCli -- --help
```

### Check Setup Status
Verify if initial admin setup is required:
```bash
dotnet run --project src/tools/AdminCli -- --status
```

### Initial Admin Setup
Create the first admin account (required before API is usable):
```bash
dotnet run --project src/tools/AdminCli -- \
  --username admin \
  --email admin@example.com \
  --password 'MySecurePassword123!' \
  --first-name John \
  --last-name Doe
```

**Requirements:**
- Username: Any string
- Email: Valid email format
- Password: Minimum 12 characters, must include uppercase, lowercase, number, special character
- First/Last name: Optional

### Custom API URL
If API is not on localhost:5245, specify the base URL:
```bash
dotnet run --project src/tools/AdminCli -- \
  --base-url http://192.168.1.100:5245 \
  --discover --range 192.168.1.0/24
```

---

**See also**: 
- Admin CLI help: `dotnet run --project src/tools/AdminCli -- --help`
- Sample CSV generation: `dotnet run --project src/tools/AdminCli -- --sample-csv --output sample.csv`
- Discovery command: `dotnet run --project src/tools/AdminCli -- --discover --range 192.168.1.0/24 --format csv --output discovered.csv`
- Setup status check: `dotnet run --project src/tools/AdminCli -- --status`

