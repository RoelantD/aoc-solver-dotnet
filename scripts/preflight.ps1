<#
.SYNOPSIS
  Preflight for the solver agent. Never submits answers and never requests locked puzzle parts.

.DESCRIPTION
  Checks the local tools, then sends the AG-UI `preflight` command to the agent and prints each check:
  the agent's identity, Cosmos DB, Key Vault (the AoC cookie), the Advent of Code session and open parts,
  both Foundry models, and the sandbox (code runs, egress blocked). Missing roles are shown as the
  Azure action that was denied.

  By default it targets the hosted agent, so the checks run under the agent's own identity.
  Use -Local for an agent started with `dotnet run --project src/Aps.Agent` (your az login identity).

.EXAMPLE
  .\scripts\preflight.ps1 -Year 2023 -Days 1-5
  .\scripts\preflight.ps1 -Local -Days 2
#>
param(
    [int]$Year = 0,
    [string]$Days = '1-25',
    [switch]$Local,
    [string]$Url = ''
)
$ErrorActionPreference = 'Stop'
$failed = 0

function Write-Ok($m) { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Write-Bad($m) { Write-Host "  [fail] $m" -ForegroundColor Red }
function Write-Skip($m) { Write-Host "  [skip] $m" -ForegroundColor Yellow }

Write-Host "`nTools" -ForegroundColor Cyan
foreach ($t in 'dotnet', 'node', 'az', 'azd') {
    if (Get-Command $t -ErrorAction SilentlyContinue) { Write-Ok $t } else { Write-Bad "$t not found"; $failed++ }
}

if (-not $Url) {
    $Url = if ($Local) { 'http://localhost:8088/invocations' }
           elseif ($env:FOUNDRY_PROJECT_ENDPOINT) { "$($env:FOUNDRY_PROJECT_ENDPOINT)/agents/aps-solver/endpoint/protocols/invocations?api-version=v1" }
           else { throw 'Set FOUNDRY_PROJECT_ENDPOINT (. .\scripts\env.ps1) or pass -Url.' }
}
$headers = @{ Accept = 'text/event-stream' }
if (-not $Local) {
    $token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    if (-not $token) { Write-Bad 'az is not logged in (az login)'; exit 1 }
    $headers.Authorization = "Bearer $token"
}

$args_ = @{ days = $Days }
if ($Year -gt 0) { $args_.year = $Year }
$body = @{
    threadId = [guid]::NewGuid().ToString(); runId = [guid]::NewGuid().ToString()
    messages = @(); state = @{}; tools = @(); context = @()
    forwardedProps = @{ command = 'preflight'; args = $args_; afterSeq = 0 }
} | ConvertTo-Json -Depth 5

Write-Host "`nAgent preflight ($(if ($Local) { 'local' } else { 'hosted' }): $Url)" -ForegroundColor Cyan
try {
    $response = Invoke-WebRequest -Uri $Url -Method Post -Headers $headers -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 900
} catch {
    Write-Bad "agent not reachable: $($_.Exception.Message)"; exit 1
}
# Read the body as UTF-8 regardless of the response charset (details contain dashes and ellipses).
$text = [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray())
$summary = $null
foreach ($line in $text -split "`n") {
    if (-not $line.StartsWith('data:')) { continue }
    $evt = $line.Substring(5) | ConvertFrom-Json
    if ($evt.type -eq 'CUSTOM' -and $evt.name -eq 'preflight.check') {
        $d = $evt.value.data
        $msg = "{0,-10} {1} ({2} ms)" -f $d.check, $d.detail, $d.ms
        if ($d.ok) { Write-Ok $msg }
        elseif ($d.skipped) { Write-Skip $msg }
        else {
            Write-Bad $msg; $failed++
            if ($d.missingPermission) { Write-Host "         missing permission: $($d.missingPermission)" -ForegroundColor Red }
        }
    } elseif ($evt.type -eq 'CUSTOM' -and $evt.name -eq 'command.error') {
        Write-Bad "agent refused: $($evt.value.message)"; $failed++
    } elseif ($evt.type -eq 'STATE_SNAPSHOT' -and $evt.snapshot.preflight) {
        $summary = $evt.snapshot.preflight
    }
}
if (-not $summary) { Write-Bad 'no preflight summary received'; $failed++ }

if ($failed) { Write-Host "`nPREFLIGHT: $failed problem(s), see above." -ForegroundColor Red; exit 1 }
Write-Host "`nPREFLIGHT: all checks passed for $($summary.year) days $($summary.dayFrom)-$($summary.dayTo)." -ForegroundColor Green
