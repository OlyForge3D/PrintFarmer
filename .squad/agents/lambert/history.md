# Project Context

- **Owner:** Jeff Papiez
- **Project:** PrintFarmer — React TypeScript dashboard for managing multiple 3D printers
- **Stack:** C# .NET 10 (API), React 19 TypeScript (Frontend), ASP.NET Core, EF Core, SignalR, Tailwind CSS, xUnit, Vitest
- **Created:** 2026-03-06

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### Controller-Repository Architecture Pattern (2025-03-05)
- **Controllers should never directly inject AppDbContext** — all database access flows through repositories/services
- Controllers remain thin: receive request → call service → return response
- Services contain business logic and coordinate repository calls
- Repositories encapsulate data access and return domain entities/DTOs
- Statistics aggregations belong in dedicated service layer (`IStatisticsService`)
- Printer existence checks use `IPrintersRepository.ExistsAsync()`
- Webhook CRUD operations use `IWebhookRepository` for all database operations
- Service registration follows pattern: repository interface/implementation in `RegisterRepositories()`, service interface/implementation in `AddPrintFarmerServices()`
- File locations: repositories in `src/infra/Repositories/{domain}/`, services in `src/infra/Services/{domain}/`
- DTOs for controller responses live in `src/infra/Dtos/` for reusability across layers
