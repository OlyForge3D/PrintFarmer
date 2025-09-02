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
