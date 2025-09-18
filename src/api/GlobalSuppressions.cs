// This file is used by Code Analysis to maintain SuppressMessage attributes that are applied to this project.
// Project-level suppressions either have no target or are given a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    category: "Naming",
    checkId: "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue naming is intentional and part of the public API surface; renaming would be a breaking change.",
    Scope = "type",
    Target = "~T:Farm.Web.Api.Services.Interfaces.IHarvestQueue")]

[assembly: SuppressMessage(
    category: "Naming",
    checkId: "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Queue naming is intentional and part of the public API surface; renaming would be a breaking change.",
    Scope = "type",
    Target = "~T:Farm.Web.Api.Services.InMemoryHarvestQueue")]

[assembly: SuppressMessage(
    "Style",
    "IDE0301:Simplify collection initialization",
    Justification = "Over simplifies code, more difficult to read.",
    Scope = "member",
    Target = "*")]
[assembly: SuppressMessage("Style", "IDE0300:Simplify collection initialization", Justification = "<Pending>", Scope = "member", Target = "~M:Farm.Web.Api.Services.NetworkUrlRewriteService.IsDockerDesktop~System.Boolean")]

[assembly: SuppressMessage(
    "Security",
    "CA3003:Review code for file path injection vulnerabilities",
    Justification = "All file and directory paths are validated, sanitized, or constructed from trusted sources throughout the codebase. Project reviewed for path injection risks.")]
