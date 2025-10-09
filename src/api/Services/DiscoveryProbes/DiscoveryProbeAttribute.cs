using System;

namespace Farm.Web.Api.Services.DiscoveryProbes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class DiscoveryProbeAttribute(string? displayName = null) : Attribute
    {
        public string? DisplayName { get; } = displayName;
    }
}
