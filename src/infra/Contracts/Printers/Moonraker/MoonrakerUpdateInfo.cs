using System.Text.Json.Serialization;

#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MoonrakerUpdateInfo
{
    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("debug_enabled")]
    public bool DebugEnabled { get; set; }

    [JsonPropertyName("is_valid")]
    public bool IsValid { get; set; }

    [JsonPropertyName("configured_type")]
    public string ConfiguredType { get; set; } = string.Empty;

    [JsonPropertyName("detected_type")]
    public string DetectedType { get; set; } = string.Empty;

    [JsonPropertyName("remote_alias")]
    public string RemoteAlias { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = string.Empty;

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = string.Empty;

    [JsonPropertyName("repo_name")]
    public string RepoName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("remote_version")]
    public string RemoteVersion { get; set; } = string.Empty;

    [JsonPropertyName("rollback_version")]
    public string RollbackVersion { get; set; } = string.Empty;

    [JsonPropertyName("current_hash")]
    public string CurrentHash { get; set; } = string.Empty;

    [JsonPropertyName("remote_hash")]
    public string RemoteHash { get; set; } = string.Empty;

    [JsonPropertyName("is_dirty")]
    public bool IsDirty { get; set; }

    [JsonPropertyName("detached")]
    public bool Detached { get; set; }

    [JsonPropertyName("commits_behind")]
    public GitCommit[] CommitsBehind { get; set; } = Array.Empty<GitCommit>();

    [JsonPropertyName("git_messages")]
    public string[] GitMessages { get; set; } = Array.Empty<string>();

    [JsonPropertyName("full_version_string")]
    public string FullVersionString { get; set; } = string.Empty;

    [JsonPropertyName("pristine")]
    public bool Pristine { get; set; }

    [JsonPropertyName("corrupt")]
    public bool Corrupt { get; set; }

    [JsonPropertyName("info_tags")]
    public string[] InfoTags { get; set; } = Array.Empty<string>();

    [JsonPropertyName("recovery_url")]
    public string RecoveryUrl { get; set; } = string.Empty;

    [JsonPropertyName("remote_url")]
    public string RemoteUrl { get; set; } = string.Empty;

    [JsonPropertyName("warnings")]
    public string[] Warnings { get; set; } = Array.Empty<string>();

    [JsonPropertyName("anomalies")]
    public string[] Anomalies { get; set; } = Array.Empty<string>();
}

#pragma warning restore CA1056
