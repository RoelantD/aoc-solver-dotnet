# Infrastructure

The agent needs two layers of Azure resources, plus the hosted agent itself.

1. **Base layer**: `main.bicep` with `modules/`. Creates a Foundry (AIServices) account with the
   workhorse and escalation model deployments, an Azure Container Apps Dynamic Sessions pool
   (PythonLTS, outbound network disabled) and roles for you as the operator. Subscription scope:

   ```powershell
   az deployment sub create -l swedencentral -f infra/main.bicep `
     -p environmentName=aps principalId=$(az ad signed-in-user show --query id -o tsv)
   ```

   Pass `existingFoundryName` and `resourceGroupName` to reuse an account that already has the
   deployments. Create a Foundry project in the account (the samples use `proj-default`).

2. **Hosted agent**: `azd deploy aps-solver`. The root `azure.yaml` is an azd *agent* project
   (`infra.provider: microsoft.foundry`). azd does not accept a Bicep provider for agent projects,
   which is why the two infrastructure layers are deployed separately.

3. **Data and roles layer**: `aps-data.bicep`, deployed by `scripts/provision-data.ps1`. Adds Cosmos DB
   (serverless, Entra only), Key Vault for the Advent of Code cookie, and role assignments for the
   hosted agent's identity. That identity exists only after the first `azd deploy`, so this layer
   comes after step 2.

## Hosted agent settings outside azure.yaml

Set these per agent in the Foundry portal (Agents → aps-solver → Settings):

- **Idle timeout**: raise it (for example to 240 minutes) so a long run is not suspended between
  dashboard requests. The default is 15 minutes.
- **Egress rules**: start in **Audit** mode, check the decisions in Application Insights, and switch
  to **Enforce** after a clean run. Allow:
  - `adventofcode.com`
  - `*.services.ai.azure.com`, `*.openai.azure.com` (models)
  - `*.dynamicsessions.io` (sandbox)
  - `*.documents.azure.com` (Cosmos DB), `*.vault.azure.net` (Key Vault)
  - `*.applicationinsights.azure.com`, `*.monitor.azure.com`, `login.microsoftonline.com` (telemetry, tokens)
