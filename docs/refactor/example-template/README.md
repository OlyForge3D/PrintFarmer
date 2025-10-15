# Refactor Example Template

This example shows a minimal, generic template for the Controllers → Services → Repositories refactor. It is intentionally generic (ExampleEntity) so teams can adapt it for their domain objects.

## Overview

- Controllers: thin, accept HTTP requests, map to service calls, return HTTP responses.
- Services: contain business logic and orchestrate repository calls.
- Repositories: encapsulate EF Core queries and data access.

Below are minimal interface and DTO examples you can copy into your project as a starting point.

---

### IExampleService (C#)

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Docs.Refactor.Example
{
    public interface IExampleService
    {
        Task<IEnumerable<ExampleDto>> GetAllAsync();
        Task<ExampleDto?> GetByIdAsync(Guid id);
        Task<ExampleDto> CreateAsync(CreateExampleRequest request);
    }
}
```

### IExampleRepository (C#)

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Docs.Refactor.Example
{
    // ExampleEntity is a placeholder for your domain entity (e.g. Printer, Model, User)
    public interface IExampleRepository
    {
        Task<IEnumerable<ExampleEntity>> GetAllAsync();
        Task<ExampleEntity?> GetByIdAsync(Guid id);
        Task<ExampleEntity> AddAsync(ExampleEntity entity);
    }
}
```

### DTOs (C#)

```csharp
using System;
namespace Docs.Refactor.Example
{
    public record ExampleDto(Guid Id, string Name, string? Description);

    public class CreateExampleRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
```

### Usage notes

- Register implementations in DI: `builder.Services.AddScoped<IExampleService, ExampleService>();`
- Keep controllers free of EF Core and business logic; call `IExampleService` from controllers and return appropriate HTTP codes.
- For repository tests, favor SQLite in-memory mode to get realistic SQL behavior.

---

Keep this file as a reference template. Copy the code into `src/api/*` when you start implementing the pattern for a real domain object.
