using System;

namespace Farm.Web.Shared.Annotations
{
    [Flags]
    public enum ImportExportTargets
    {
        None = 0,
        Import = 1,
        Export = 2,
        Both = Import | Export
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ImportExportAttribute : Attribute
    {
        public ImportExportTargets IgnoreFor { get; }

        public ImportExportAttribute(ImportExportTargets ignoreFor = ImportExportTargets.Both)
        {
            IgnoreFor = ignoreFor;
        }
    }
}
