$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\wsl-transport.ps1')
$values = @('literal $HOME ${name} $(command)', 'quotes "double" ''single''', 'spaces and ; & | >',
    "first line`nsecond line`n", '', 'Unicode: café')
$expected = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($values | ConvertTo-Json -Compress)))
$script = @'
set -euo pipefail
python3 -c 'import base64,json,sys; expected=json.loads(base64.b64decode(sys.argv[1])); assert sys.argv[2:]==expected, repr(sys.argv[2:])' "$@"
'@
$code = Invoke-NativeWsl -Script $script.Replace("`r`n", "`n") -Arguments (@($expected) + $values)
if ($code -ne 0) { throw "Literal argument transport failed: $code" }
$code = Invoke-NativeWsl -Script "printf '%s\n' 'literal `$value ""quoted""' >/dev/null`nexit 23"
if ($code -ne 23) { throw "Child exit status lost: $code" }
$code = Invoke-NativeWsl -Script 'set -euo pipefail; false | tee /dev/null'
if ($code -eq 0) { throw 'Pipeline failure was masked' }
Write-Output 'WSL transport: literal content, multiline arguments, errors and pipeline status passed'
