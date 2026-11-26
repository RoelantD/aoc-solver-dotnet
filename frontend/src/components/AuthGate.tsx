import type { AccountInfo } from '@azure/msal-browser'
import { LogIn, LogOut, Sparkles } from 'lucide-react'
import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { authEnabled, initAuth, login, logout } from '../lib/auth'
import { Button } from './ui'

const AccountCtx = createContext<AccountInfo | null>(null)

/** Signed-in account (null for a local agent, where sign-in is skipped). */
function useAccount() {
  return useContext(AccountCtx)
}

/** Requires Entra sign-in before rendering the app when the agent is hosted. A local agent skips this. */
export function AuthGate({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{ ready: boolean; account: AccountInfo | null; error: string | null }>({
    ready: !authEnabled,
    account: null,
    error: null,
  })
  useEffect(() => {
    if (!authEnabled) return
    initAuth()
      .then((account) => setState({ ready: true, account, error: null }))
      .catch((e: Error) => setState({ ready: true, account: null, error: e.message }))
  }, [])

  if (!authEnabled) return <>{children}</>
  if (!state.ready) return <div className="flex h-full items-center justify-center text-ink-400">Signing in…</div>
  if (!state.account) {
    return (
      <div className="m-auto flex h-full max-w-md flex-col items-center justify-center gap-4 p-10 text-center">
        <Sparkles className="text-star" size={32} />
        <h1 className="text-xl font-semibold">Agentic Puzzle Solver</h1>
        <p className="text-sm text-ink-400">The hosted agent requires a Microsoft Entra sign-in.</p>
        {state.error && <p className="text-sm text-bad">{state.error}</p>}
        <Button variant="primary" onClick={() => login().catch((e: Error) => setState((s) => ({ ...s, error: e.message })))}>
          <LogIn size={16} /> Sign in
        </Button>
      </div>
    )
  }
  return <AccountCtx.Provider value={state.account}>{children}</AccountCtx.Provider>
}

export function AccountBadge() {
  const account = useAccount()
  if (!account) return null
  return (
    <Button variant="ghost" title={`Signed in as ${account.username}. Click to sign out.`} onClick={() => void logout()}>
      <LogOut size={16} /> {account.name ?? account.username}
    </Button>
  )
}
