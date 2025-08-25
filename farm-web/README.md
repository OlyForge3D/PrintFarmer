# Print Farm Web (Blazor + ASP.NET Core)

Minimal hosted solution to manage a Klipper/Moonraker-based print farm:
- Add/remove printers and spools
- View printer online status
- Send commands: home axes, set temps, relative moves

Server: ASP.NET Core REST API with EF Core SQLite.
Client: Blazor WebAssembly.

## Quick start

1. Ensure .NET 8 SDK is installed.
2. Restore & run Server API.

### Powershell

```
# from repo root
cd .\farm-web\Server

# restore
 dotnet restore

# run API
 dotnet run
```

API will start at http://localhost:5088 with Swagger UI.

3. Run the Blazor client (separately) for dev hot reload:

```
cd ..\Client
 dotnet run
```

Then open http://localhost:5000 (port chosen by dev server). For a simple setup you can also host the static client via any static server and point it to the API base.

## Config
- Connection string: `Server/appsettings.json` (SQLite file farm.db in Server folder by default)
- Moonraker URL: set per printer (e.g., http://192.168.1.50:7125)

## Notes
- This is a minimal scaffold. You may want auth, validation, richer status, and WS events later.
