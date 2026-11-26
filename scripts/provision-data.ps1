<#
.SYNOPSIS
  Provisions the data and roles layer (Cosmos DB, Key Vault, role assignments) for the hosted agent.

.DESCRIPTION
  Run after the first `azd deploy aps-solver`, because the hosted agent's identity exists only then.
  Deploys infra/aps-data.bicep into the resource group, then writes COSMOS_ENDPOINT and KEYVAULT_URI to the
  azd environment so the next `azd deploy` passes them to the agent. Never handles the Advent of Code cookie:
  set it yourself with `az keyvault secret set` (see the output).

.PARAMETER ResourceGroup
  Resource group that holds the Foundry account and the session pool.

.PARAMETER SkipModelAndSandboxRoles
  Use when Foundry User and Session Executor were already granted to the agent identity by hand.
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-aigrunn',
    [string] $AgentName = 'aps-solver',
    [string] $FoundryAccountName = 'aif-grunn',
    [switch] $SkipModelAndSandboxRoles,
    [switch] $SkipDeveloperRoles
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host "Reading the hosted agent identity for '$AgentName'..."
$agent = azd ai agent show $AgentName -o json | ConvertFrom-Json
$agentPrincipalId = $agent.instance_identity.principal_id
if (-not $agentPrincipalId) { throw "No instance identity found. Run 'azd deploy $AgentName' first." }

$pool = az resource list -g $ResourceGroup --resource-type Microsoft.App/sessionPools --query '[0].name' -o tsv
if (-not $pool) { throw "No Dynamic Sessions pool found in $ResourceGroup." }

$developerPrincipalId = ''
if (-not $SkipDeveloperRoles) { $developerPrincipalId = az ad signed-in-user show --query id -o tsv }

Write-Host "Deploying infra/aps-data.bicep to $ResourceGroup (pool: $pool)..."
$outputs = az deployment group create -g $ResourceGroup -n aps-data -f infra/aps-data.bicep `
    -p agentPrincipalId=$agentPrincipalId developerPrincipalId=$developerPrincipalId `
       foundryAccountName=$FoundryAccountName sessionPoolName=$pool `
       assignModelAndSandboxRoles=$(-not $SkipModelAndSandboxRoles.IsPresent) `
    --query properties.outputs -o json | ConvertFrom-Json

azd env set COSMOS_ENDPOINT $outputs.COSMOS_ENDPOINT.value | Out-Null
azd env set KEYVAULT_URI $outputs.KEYVAULT_URI.value | Out-Null

Write-Host ''
Write-Host "Cosmos DB : $($outputs.COSMOS_ENDPOINT.value)"
Write-Host "Key Vault : $($outputs.KEYVAULT_URI.value)"
Write-Host "Agent principal: $agentPrincipalId"
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. Store the Advent of Code session cookie yourself (never paste it into chat or a file):"
Write-Host "       az keyvault secret set --vault-name $($outputs.KEYVAULT_NAME.value) --name aoc-session --value <cookie>"
Write-Host "  2. azd deploy $AgentName   (passes COSMOS_ENDPOINT and KEYVAULT_URI to the agent)"
Write-Host '  3. Allow about 15 minutes for the Foundry User role to reach the Claude endpoint, then run preflight.'
