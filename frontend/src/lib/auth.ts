// Entra sign-in for the hosted agent. A Foundry hosted agent's Invocations endpoint expects a bearer token for the
// audience https://ai.azure.com, acquired here with MSAL (redirect flow, cache in sessionStorage). A local agent
// (VITE_AGENT_URL on localhost) needs no sign-in at all, so every function below is a no-op for it.
import {
  type AccountInfo,
  InteractionRequiredAuthError,
  type IPublicClientApplication,
  createStandardPublicClientApplication,
} from '@azure/msal-browser'

const AGENT_SCOPE = 'https://ai.azure.com/.default'
const DEFAULT_AGENT_URL = 'http://localhost:8088/invocations'

/** Full Invocations URL of the agent (hosted: `{projectEndpoint}/agents/aps-solver/endpoint/protocols/invocations?api-version=v1`). */
export const AGENT_URL: string = (import.meta.env.VITE_AGENT_URL as string | undefined)?.trim() || DEFAULT_AGENT_URL

/** A developer-machine agent: no Entra sign-in and no hosted session affinity. */
export function isLocalAgentUrl(url: string): boolean {
  return url.startsWith('http://localhost') || url.startsWith('http://127.0.0.1')
}

export const authEnabled = !isLocalAgentUrl(AGENT_URL)

let pca: Promise<IPublicClientApplication> | null = null

/** The MSAL client, created on first use so a local setup never needs the Entra settings. */
function client(): Promise<IPublicClientApplication> {
  if (!pca) {
    const clientId = import.meta.env.VITE_ENTRA_CLIENT_ID as string | undefined
    const tenantId = import.meta.env.VITE_ENTRA_TENANT_ID as string | undefined
    if (!clientId || !tenantId) {
      return Promise.reject(new Error('VITE_ENTRA_CLIENT_ID and VITE_ENTRA_TENANT_ID must be set to use a hosted agent'))
    }
    pca = createStandardPublicClientApplication({
      auth: {
        clientId,
        authority: `https://login.microsoftonline.com/${tenantId}`,
        redirectUri: window.location.origin,
      },
      cache: { cacheLocation: 'sessionStorage' },
    })
  }
  return pca
}

/**
 * Completes a pending redirect and returns the signed-in account, or null when nobody is signed in.
 * Always resolves to null when auth is disabled (local agent).
 */
export async function initAuth(): Promise<AccountInfo | null> {
  if (!authEnabled) return null
  const app = await client()
  const result = await app.handleRedirectPromise()
  const account = result?.account ?? app.getActiveAccount() ?? app.getAllAccounts()[0] ?? null
  if (account) app.setActiveAccount(account)
  return account
}

export async function login(): Promise<void> {
  if (!authEnabled) return
  const app = await client()
  await app.loginRedirect({ scopes: [AGENT_SCOPE] })
}

export async function logout(): Promise<void> {
  if (!authEnabled) return
  const app = await client()
  await app.logoutRedirect({ account: app.getActiveAccount() ?? undefined })
}

/**
 * Bearer token for the agent, or null when auth is disabled or nobody is signed in. MSAL serves it from its cache and
 * refreshes it silently; when that needs user interaction (expired session, new consent) the page redirects to sign-in.
 */
export async function getToken(): Promise<string | null> {
  if (!authEnabled) return null
  const app = await client()
  const account = app.getActiveAccount() ?? app.getAllAccounts()[0]
  if (!account) return null
  try {
    const result = await app.acquireTokenSilent({ scopes: [AGENT_SCOPE], account })
    return result.accessToken
  } catch (e) {
    if (e instanceof InteractionRequiredAuthError) {
      await app.acquireTokenRedirect({ scopes: [AGENT_SCOPE], account })
      return null
    }
    throw e
  }
}
