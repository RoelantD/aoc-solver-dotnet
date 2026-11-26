# Loads the repo's .env into the CURRENT shell, for the samples and the local agent.
# Dot-source it (note the leading dot), once per terminal:
#
#   . .\scripts\env.ps1
#
# AOC_SESSION is skipped on purpose: the agent reads it from .env itself, and the samples never need it.
# If .env has no SESSIONS_POOL_ENDPOINT, it is read from the azd environment 'aps'.
$envFile = Join-Path $PSScriptRoot '..\.env'
if (-not (Test-Path $envFile)) {
    Write-Warning ".env not found; copy .env.example to .env and fill it in"
} else {
    foreach ($line in Get-Content $envFile) {
        if ($line -match '^\s*([A-Z][A-Z0-9_]*)\s*=\s*(.*?)\s*$' -and $Matches[1] -ne 'AOC_SESSION') {
            Set-Item "Env:$($Matches[1])" $Matches[2].Trim('"')
        }
    }
}
if (-not $env:SESSIONS_POOL_ENDPOINT -or $env:SESSIONS_POOL_ENDPOINT -match '<') {
    $env:SESSIONS_POOL_ENDPOINT = (azd env get-value SESSIONS_POOL_ENDPOINT -e aps)
}
foreach ($name in 'FOUNDRY_PROJECT_ENDPOINT', 'FOUNDRY_RESOURCE_NAME', 'SESSIONS_POOL_ENDPOINT') {
    $value = (Get-Item "Env:$name" -ErrorAction SilentlyContinue).Value
    if ($value) { Write-Host ("{0,-25}= {1}" -f $name, $value) } else { Write-Warning "$name is not set" }
}
