// Data and roles layer for the hosted agent. Deployed by scripts/provision-data.ps1 after the first
// `azd deploy`, because the hosted agent's Entra identity exists only then.
// - Cosmos DB for NoSQL (serverless, Entra only): runs, parts, events, ledger, cache and checkpoints containers
// - Key Vault (RBAC) for the Advent of Code session cookie
// - Role assignments for the agent identity (and the deploying developer, for local runs)

@description('Location for new resources.')
param location string = resourceGroup().location

@description('Object id of the hosted agent identity (azd ai agent show).')
param agentPrincipalId string

@description('Object id of the developer running the agent locally (az ad signed-in-user show). Empty to skip.')
param developerPrincipalId string = ''

@description('Existing Foundry account (models).')
param foundryAccountName string = 'aif-grunn'

@description('Existing Dynamic Sessions pool (generated code).')
param sessionPoolName string

@description('Assign model and sandbox roles to the agent. Set false when they were granted by hand already (role assignment names differ).')
param assignModelAndSandboxRoles bool = true

param nameSuffix string = uniqueString(resourceGroup().id)

var cosmosName = 'cosmos-aps-${nameSuffix}'
var vaultName = take('kv-aps-${nameSuffix}', 24)

var containers = [
  { name: 'runs', pk: '/id' }
  { name: 'parts', pk: '/runId' }
  { name: 'events', pk: '/runId' }
  { name: 'ledger', pk: '/year' }
  { name: 'checkpoints', pk: '/runId' }
  { name: 'cache', pk: '/year' }
]

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: cosmosName
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true // Entra only: no keys anywhere
    capabilities: [ { name: 'EnableServerless' } ]
    locations: [ { locationName: location, failoverPriority: 0 } ]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    minimalTlsVersion: 'Tls12'
  }
}

resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: 'aps'
  properties: { resource: { id: 'aps' } }
}

resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in containers: {
  parent: database
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: { paths: [ c.pk ], kind: 'Hash' }
    }
  }
}]

// Cosmos DB Built-in Data Contributor (data plane role, not Azure RBAC)
var cosmosDataContributor = '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'

resource agentCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmos
  name: guid(cosmos.id, agentPrincipalId, 'data-contributor')
  properties: { principalId: agentPrincipalId, roleDefinitionId: cosmosDataContributor, scope: cosmos.id }
}

resource developerCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = if (!empty(developerPrincipalId)) {
  parent: cosmos
  name: guid(cosmos.id, developerPrincipalId, 'data-contributor')
  properties: { principalId: developerPrincipalId, roleDefinitionId: cosmosDataContributor, scope: cosmos.id }
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: vaultName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var foundryUser = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var sessionExecutor = '0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0'

resource agentVault 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, agentPrincipalId, keyVaultSecretsUser)
  properties: {
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
  }
}

resource developerVault 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  scope: vault
  name: guid(vault.id, developerPrincipalId, keyVaultSecretsOfficer)
  properties: {
    principalId: developerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
  }
}

resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource pool 'Microsoft.App/sessionPools@2025-07-01' existing = {
  name: sessionPoolName
}

// Claude on Foundry goes through the account-level /anthropic route (identity spike, 2026-11-24).
// Foundry User is broader than needed (it includes listkeys); use a custom data-action role in production.
resource agentFoundry 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignModelAndSandboxRoles) {
  scope: foundry
  name: guid(foundry.id, agentPrincipalId, foundryUser)
  properties: {
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUser)
  }
}

resource agentPool 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignModelAndSandboxRoles) {
  scope: pool
  name: guid(pool.id, agentPrincipalId, sessionExecutor)
  properties: {
    principalId: agentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sessionExecutor)
  }
}

output COSMOS_ENDPOINT string = cosmos.properties.documentEndpoint
output KEYVAULT_URI string = vault.properties.vaultUri
output KEYVAULT_NAME string = vault.name
