using System.Reflection;

namespace Farm.Web.Api.Health;

/// <summary>
/// Parses the assembly <see cref="AssemblyInformationalVersionAttribute"/> into a
/// semantic version and an optional git commit SHA. The informational version is
/// formatted as <c>&lt;version&gt;+&lt;sha&gt;</c> (see the repository root
/// <c>Directory.Build.props</c>), so anything after the first <c>+</c> is treated as
/// the commit identifier.
/// </summary>
internal static class BuildVersion
{
    internal const string UnknownVersion = "0.0.0";

    /// <summary>
    /// Splits an informational version string into its version and commit parts.
    /// </summary>
    /// <param name="informationalVersion">
    /// The raw <c>AssemblyInformationalVersion</c> value, e.g. <c>0.2.2+de53651</c>.
    /// </param>
    /// <returns>
    /// A tuple of the version (never null/empty; falls back to <see cref="UnknownVersion"/>)
    /// and the commit SHA (null when no <c>+&lt;sha&gt;</c> suffix is present).
    /// </returns>
    public static (string Version, string? Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return (UnknownVersion, null);
        }

        string[] parts = informationalVersion.Split('+', 2);
        string version = string.IsNullOrWhiteSpace(parts[0]) ? UnknownVersion : parts[0];
        string? commit = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1]
            : null;

        return (version, commit);
    }

    /// <summary>
    /// Reads and parses the informational version of the given assembly (defaults to the
    /// entry assembly).
    /// </summary>
    public static (string Version, string? Commit) FromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly();
        string? informationalVersion = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return Parse(informationalVersion);
    }
}
