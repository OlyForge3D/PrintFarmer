// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("StyleCop.CSharp.MaintainabilityRules", "SA1407:Arithmetic expressions should declare precedence", Justification = "Standard arithmetic operators follow well-understood precedence rules", Scope = "member", Target = "~M:Farm.OrcaSlicer.Worker.Services.OrcaSlicingPipelineService.ExtractGcodeMetadataAsync(System.String,System.Threading.CancellationToken)~System.Threading.Tasks.Task{Farm.OrcaSlicer.Worker.Services.OrcaSlicingPipelineService.GcodeMetadata}")]
[assembly: SuppressMessage("StyleCop.CSharp.NamingRules", "SA1311:Static readonly fields should begin with upper-case letter", Justification = "s_ prefix follows .NET runtime team naming convention for static fields", Scope = "member", Target = "~F:Farm.OrcaSlicer.Worker.Services.ProfilePreloadService.s_jsonOptions")]
[assembly: SuppressMessage("StyleCop.CSharp.NamingRules", "SA1308:Variable names should not be prefixed", Justification = "s_ prefix follows .NET runtime team naming convention for static fields", Scope = "member", Target = "~F:Farm.OrcaSlicer.Worker.Services.ProfilePreloadService.s_jsonOptions")]
