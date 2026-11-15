param([string] $Repository, [string] $Root, [int] $Launcher, [string] $RunId)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;

public sealed class SettingsSourceGuard : IDisposable
{
    private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
    private readonly HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new object();
    private readonly string repository;
    private readonly string failure;
    private readonly string runId;

    public SettingsSourceGuard(string repository, string root, string runId, string[] inputs)
    {
        this.repository = Path.GetFullPath(repository).TrimEnd('\\') + "\\";
        this.failure = Path.Combine(root, "source-guard-failed.json");
        this.runId = runId;
        directories.Add(this.repository.TrimEnd('\\'));
        foreach (string input in inputs)
        {
            string file = Path.GetFullPath(Path.Combine(repository, input));
            files.Add(file);
            string directory = Path.GetDirectoryName(file);
            while (directory != null && directory.StartsWith(this.repository, StringComparison.OrdinalIgnoreCase))
            {
                directories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }
        foreach (string directory in directories)
        {
            var watcher = new FileSystemWatcher(directory);
            watcher.IncludeSubdirectories = false;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.InternalBufferSize = 65536;
            watcher.Changed += (sender, args) => Changed(args.FullPath, false);
            watcher.Created += (sender, args) => Changed(args.FullPath, true);
            watcher.Deleted += (sender, args) => Changed(args.FullPath, true);
            watcher.Renamed += (sender, args) => { Changed(args.OldFullPath, true); Changed(args.FullPath, true); };
            watcher.Error += (sender, args) => Fail("Source watcher failed: " + args.GetException().Message);
            watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
    }

    private void Changed(string path, bool rename)
    {
        if (String.IsNullOrEmpty(path)) { Fail("Source watcher returned an event without a path"); return; }
        if (files.Contains(path) || (rename && directories.Contains(path)))
            Fail("Tracked source changed during this run: " + path);
    }

    private void Fail(string reason)
    {
        lock (gate)
        {
            if (!File.Exists(failure)) File.WriteAllText(failure, runId + ": " + reason);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in watchers) watcher.Dispose();
    }
}
'@

$inputs = [string[]](Get-Content -LiteralPath (Join-Path $Root 'source-guard-inputs.json') -Raw | ConvertFrom-Json)
$guard = [SettingsSourceGuard]::new($Repository, $Root, $RunId, $inputs)
try {
    @{ runId = $RunId; pid = $PID } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath (Join-Path $Root 'source-guard-ready.json') -Encoding Ascii
    while (Get-Process -Id $Launcher -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
} finally {
    $guard.Dispose()
}
