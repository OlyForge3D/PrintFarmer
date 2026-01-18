using System;

namespace Farm.Infrastructure.Annotations;

[Flags]
public enum ImportExportTargets
{
    None = 0,
    Import = 1,
    Export = 2,
    Both = Import | Export
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ImportExportAttribute(ImportExportTargets ignoreFor = ImportExportTargets.Both) : Attribute
{
    public ImportExportTargets IgnoreFor { get; } = ignoreFor;
}
