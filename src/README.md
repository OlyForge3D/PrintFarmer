# ForgeIQ Web (Blazor + ASP.NET Core)

Hosted solution to manage a Klipper/Moonraker-based print farm:
- Add/remove printers and spools
- Live status via SignalR
- History totals and thumbnails

Server: ASP.NET Core REST API with EF Core. Client: Blazor WebAssembly.

Important: Run all dotnet commands from the src folder.

## Quick start (local dev)

1) Prereqs: .NET SDK 8+.

2) Restore, build, and run the hosted server (serves client + API):

```
# from repo root
cd ./src
dotnet restore ./farm-web.sln
dotnet build ./farm-web.sln -c Debug
dotnet run --project ./server/Farm.Web.Server.csproj
```

API at http://localhost:5088 with Swagger UI; health at /healthz.

Hot reload:
```
cd ./src
dotnet watch --project ./server/Farm.Web.Server.csproj run
```

## Multi-database support

Providers supported: Sqlite (default), SqlServer, Postgres, MySql.

Selection order:
- Configuration key Db:Provider
- Env var DB_PROVIDER
- Defaults to Sqlite

Connection strings come from appsettings.json (ConnectionStrings) and can be overridden by env vars, e.g. ConnectionStrings__Postgres.

Examples:
- DB_PROVIDER=Sqlite, ConnectionStrings__Default=Data Source=farm.db
- DB_PROVIDER=Postgres, ConnectionStrings__Postgres=Host=localhost;Database=forgeiq;Username=postgres;Password=postgres
- DB_PROVIDER=SqlServer, ConnectionStrings__SqlServer=Server=localhost;Database=forgeiq;Trusted_Connection=True;TrustServerCertificate=True;
- DB_PROVIDER=MySql, ConnectionStrings__MySql=Server=localhost;Database=forgeiq;User=root;Password=example;

Init mode (optional):
- DB_INIT_MODE=EnsureCreated to skip EF migrations and create schema if missing.
- Otherwise, Sqlite runs migrations; other providers currently call EnsureCreated.

## Docker Compose (2 containers)

Production-like split between static web (Nginx + HTTPS) and API:

```
docker compose up -d --build
```

Endpoints:
- Web: http://localhost:8081 and https://localhost:8443
- API (direct): http://localhost:5088

Switch DB in compose by setting DB_PROVIDER and corresponding ConnectionStrings__... (see comments in docker-compose.yml).

## Config
- Connection strings: src/server/appsettings.json (overridable via env)
- CORS: ALLOWED_ORIGINS env (comma-separated)
- Moonraker/PrusaLink base URLs: configured per printer

## Tests
```
cd ./src
dotnet test ./farm-web.sln -c Debug
```
