Set-StrictMode -Version Latest

function Invoke-NativeWsl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Script,
        [string[]] $Arguments = @()
    )

    # Only base64 JSON crosses stdin. Neither shell parses caller-controlled text.
    $payload = @{ script = $Script; arguments = @($Arguments) } | ConvertTo-Json -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $bootstrap = 'import base64,json,os,sys;p=json.loads(base64.b64decode(sys.stdin.buffer.read(),validate=True));os.execv("/bin/bash",["bash","-lc",p["script"],"daily-validation",*p["arguments"]])'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'wsl.exe'
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    foreach ($argument in @('-d', 'Ubuntu-24.04', '--', 'bash', '-lc', "exec /usr/bin/python3 -c '$bootstrap'")) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Write($encoded)
        $process.StandardInput.Close()
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}
