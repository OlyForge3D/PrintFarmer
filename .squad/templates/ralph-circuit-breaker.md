# Ralph Circuit Breaker — Model Rate Limit Fallback

> Classic circuit breaker behavior applied to Copilot model selection.
> When the preferred model is rate limited, Ralph follows a bounded fallback chain, omits the model after exhaustion, and probes recovery after cooldown.

## Problem

When multiple Ralph instances run across repositories, model rate limits can cause cascading failures. The circuit breaker limits retries, uses the fast/cheap policy fallback chain, and preserves a reachable recovery path without making pricing or quota claims.

## Circuit Breaker States

```
┌─────────┐   rate limit    ┌────────┐   chain exhausted   ┌───────────┐
│ CLOSED  │ ──────────────► │  OPEN  │ ──────────────────► │ EXHAUSTED │
│ normal  │                 │fallback│                     │omit model │
└────┬────┘                 └───┬────┘                     └─────┬─────┘
     ▲                          │ cooldown                         │ cooldown
     │ 2 consecutive successes │ expires                          │ expires
     │                          ▼                                  ▼
     └──────────────────── ┌───────────┐ ◄─────────────────────────┘
                          │ HALF-OPEN │
                          │   probe   │
                          └───────────┘
                                │ rate limit
                                └──────────────► OPEN
```

### CLOSED (normal operation)

- Use the preferred model from configuration
- Every successful response confirms the circuit stays closed
- On a rate-limit error, transition to OPEN

### OPEN (fallback active)

- Fall back through this fast/cheap policy chain:
  1. `gpt-5.6-luna`
  2. `gemini-3.5-flash`
  3. `claude-haiku-4.5`
  4. `gpt-5.4-mini`
- Make at most three retries after the first fallback attempt
- Start the cooldown timer (default: 10 minutes)
- When cooldown expires, transition to HALF-OPEN
- When the fallback chain is exhausted, transition to EXHAUSTED

### EXHAUSTED (platform default)

- Omit the model parameter entirely so the platform chooses its runtime default
- Do not reset `openedAt` while EXHAUSTED; the original cooldown remains reachable
- When cooldown expires, transition to HALF-OPEN and probe the preferred model

### HALF-OPEN (testing recovery)

- Try the preferred model again
- If two consecutive calls succeed, transition to CLOSED
- If a rate-limit error occurs, transition to OPEN and restart the cooldown

Runtime rejection overrides the static model catalog. Model IDs remain separate from reasoning-effort and context-window parameters; drop unsupported parameters when changing models.

## State File: `.squad/ralph-circuit-breaker.json`

```json
{
  "state": "closed",
  "preferredModel": "claude-sonnet-5",
  "fallbackChain": ["gpt-5.6-luna", "gemini-3.5-flash", "claude-haiku-4.5", "gpt-5.4-mini"],
  "currentFallbackIndex": 0,
  "cooldownMinutes": 10,
  "openedAt": null,
  "halfOpenSuccesses": 0,
  "consecutiveFailures": 0,
  "metrics": {
    "totalFallbacks": 0,
    "totalRecoveries": 0,
    "lastFallbackAt": null,
    "lastRecoveryAt": null
  }
}
```

## PowerShell Functions

Paste these into `ralph-watch.ps1` or source them from a shared module.

### `Get-CircuitBreakerState`

```powershell
function Get-CircuitBreakerState {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    if (-not (Test-Path $StateFile)) {
        $default = @{
            state                = "closed"
            preferredModel       = "claude-sonnet-5"
            fallbackChain        = @("gpt-5.6-luna", "gemini-3.5-flash", "claude-haiku-4.5", "gpt-5.4-mini")
            currentFallbackIndex = 0
            cooldownMinutes      = 10
            openedAt             = $null
            halfOpenSuccesses    = 0
            consecutiveFailures  = 0
            metrics              = @{
                totalFallbacks  = 0
                totalRecoveries = 0
                lastFallbackAt  = $null
                lastRecoveryAt  = $null
            }
        }
        $default | ConvertTo-Json -Depth 3 | Set-Content $StateFile
        return $default
    }

    return (Get-Content $StateFile -Raw | ConvertFrom-Json)
}
```

### `Save-CircuitBreakerState`

```powershell
function Save-CircuitBreakerState {
    param(
        [object]$State,
        [string]$StateFile = ".squad/ralph-circuit-breaker.json"
    )

    $State | ConvertTo-Json -Depth 3 | Set-Content $StateFile
}
```

### `Get-CurrentModel`

Returns the model Ralph should use. A `$null` return value means the caller must omit the model parameter.

```powershell
function Get-CurrentModel {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile

    if ($cb.state -eq "open" -or $cb.state -eq "exhausted") {
        if ($cb.openedAt) {
            $opened = [DateTime]::Parse($cb.openedAt)
            $elapsed = (Get-Date) - $opened
            if ($elapsed.TotalMinutes -ge $cb.cooldownMinutes) {
                $cb.state = "half-open"
                $cb.halfOpenSuccesses = 0
                Save-CircuitBreakerState -State $cb -StateFile $StateFile
                Write-Host "  [circuit-breaker] Cooldown expired. Testing preferred model..." -ForegroundColor Yellow
                return $cb.preferredModel
            }
        }

        if ($cb.state -eq "exhausted") {
            return $null
        }

        if (-not $cb.fallbackChain -or $cb.fallbackChain.Count -eq 0) {
            $cb.state = "exhausted"
            Save-CircuitBreakerState -State $cb -StateFile $StateFile
            return $null
        }

        $idx = [Math]::Min($cb.currentFallbackIndex, $cb.fallbackChain.Count - 1)
        return $cb.fallbackChain[$idx]
    }

    if ($cb.state -eq "half-open") {
        return $cb.preferredModel
    }

    return $cb.preferredModel
}
```

### `Update-CircuitBreakerOnSuccess`

Call after every successful model response.

```powershell
function Update-CircuitBreakerOnSuccess {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile
    $cb.consecutiveFailures = 0

    if ($cb.state -eq "half-open") {
        $cb.halfOpenSuccesses++
        if ($cb.halfOpenSuccesses -ge 2) {
            $cb.state = "closed"
            $cb.openedAt = $null
            $cb.halfOpenSuccesses = 0
            $cb.currentFallbackIndex = 0
            $cb.metrics.totalRecoveries++
            $cb.metrics.lastRecoveryAt = (Get-Date).ToString("o")
            Save-CircuitBreakerState -State $cb -StateFile $StateFile
            Write-Host "  [circuit-breaker] RECOVERED — back to preferred model ($($cb.preferredModel))" -ForegroundColor Green
            return
        }

        Save-CircuitBreakerState -State $cb -StateFile $StateFile
        Write-Host "  [circuit-breaker] Half-open success $($cb.halfOpenSuccesses)/2" -ForegroundColor Yellow
    }
}
```

### `Update-CircuitBreakerOnRateLimit`

Call when a model response indicates rate limiting (HTTP 429 or an error message containing "rate limit").

```powershell
function Update-CircuitBreakerOnRateLimit {
    param([string]$StateFile = ".squad/ralph-circuit-breaker.json")

    $cb = Get-CircuitBreakerState -StateFile $StateFile
    $cb.consecutiveFailures++

    if ($cb.state -eq "closed" -or $cb.state -eq "half-open") {
        $cb.state = "open"
        $cb.openedAt = (Get-Date).ToString("o")
        $cb.halfOpenSuccesses = 0
        $cb.currentFallbackIndex = 0
        $cb.metrics.totalFallbacks++
        $cb.metrics.lastFallbackAt = (Get-Date).ToString("o")
        Save-CircuitBreakerState -State $cb -StateFile $StateFile

        $fallbackModel = $cb.fallbackChain[0]
        Write-Host "  [circuit-breaker] RATE LIMITED — falling back to $fallbackModel (cooldown: $($cb.cooldownMinutes)m)" -ForegroundColor Red
        return
    }

    if ($cb.state -eq "open") {
        if ($cb.currentFallbackIndex -lt ($cb.fallbackChain.Count - 1)) {
            $cb.currentFallbackIndex++
            $cb.openedAt = (Get-Date).ToString("o")
            $nextModel = $cb.fallbackChain[$cb.currentFallbackIndex]
            Write-Host "  [circuit-breaker] Fallback also limited — trying $nextModel" -ForegroundColor Red
        } else {
            $cb.state = "exhausted"
            Write-Host "  [circuit-breaker] Fallback chain exhausted — omitting the model parameter until cooldown expires" -ForegroundColor Red
        }

        Save-CircuitBreakerState -State $cb -StateFile $StateFile
        return
    }

    if ($cb.state -eq "exhausted") {
        Save-CircuitBreakerState -State $cb -StateFile $StateFile
    }
}
```

While EXHAUSTED, `Update-CircuitBreakerOnRateLimit` persists failure counters without changing `openedAt`; repeated runtime-default failures therefore cannot postpone the HALF-OPEN recovery probe forever.

## Integration with `ralph-watch.ps1`

In the polling loop, omit `--model` when `Get-CurrentModel` returns `$null`:

```powershell
$model = Get-CurrentModel

if ($null -eq $model) {
    $result = copilot-cli ...
} else {
    $result = copilot-cli --model $model ...
}

if ($result -match "rate.?limit" -or $LASTEXITCODE -eq 429) {
    Update-CircuitBreakerOnRateLimit
} else {
    Update-CircuitBreakerOnSuccess
}
```

### Full integration example

```powershell
. .squad-templates/ralph-circuit-breaker-functions.ps1

while ($true) {
    $model = Get-CurrentModel
    $invokeParams = @{}
    if ($null -ne $model) {
        $invokeParams.Model = $model
        Write-Host "Polling with model: $model"
    } else {
        Write-Host "Polling with platform default (model parameter omitted)"
    }

    try {
        $response = Invoke-RalphCycle @invokeParams
        Update-CircuitBreakerOnSuccess
    }
    catch {
        if ($_.Exception.Message -match "rate.?limit|429|quota|Too Many Requests") {
            Update-CircuitBreakerOnRateLimit
            continue
        }
        throw
    }

    Start-Sleep -Seconds $pollInterval
}
```

## Configuration

Override defaults by editing `.squad/ralph-circuit-breaker.json`:

| Field | Default | Description |
|-------|---------|-------------|
| `preferredModel` | `claude-sonnet-5` | Model to use when the circuit is closed |
| `fallbackChain` | `gpt-5.6-luna`, `gemini-3.5-flash`, `claude-haiku-4.5`, `gpt-5.4-mini` | Ordered fast/cheap policy fallbacks |
| `cooldownMinutes` | `10` | How long to wait before testing recovery |

## Metrics

The state file tracks operational metrics:

- **totalFallbacks** — How many times the circuit opened
- **totalRecoveries** — How many times it recovered to the preferred model
- **lastFallbackAt** — ISO timestamp of the last rate-limit event
- **lastRecoveryAt** — ISO timestamp of the last successful recovery

Query metrics with:

```powershell
$cb = Get-Content .squad/ralph-circuit-breaker.json | ConvertFrom-Json
Write-Host "Fallbacks: $($cb.metrics.totalFallbacks) | Recoveries: $($cb.metrics.totalRecoveries)"
```
