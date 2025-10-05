using System;

namespace Farm.Web.Api.Services.DiscoveryProbes
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class DiscoveryProbeAttribute : Attribute
    {
        public string? DisplayName { get; }
        public DiscoveryProbeAttribute(string? displayName = null)
        {
            DisplayName = displayName;
        }
    }
}
