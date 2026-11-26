import { CheckCircle2, CircleSlash, Loader2, Stethoscope, XCircle } from 'lucide-react'
import { useState } from 'react'
import { type PreflightCheck, type PreflightSummary, preflight } from '../api/agent'
import { Button } from './ui'

const LABELS: Record<string, string> = {
  identity: 'Agent identity',
  store: 'Run store',
  keyvault: 'AoC session secret',
  website: 'Advent of Code',
  workhorse: 'Workhorse model',
  escalation: 'Escalation model',
  sandbox: 'Sandbox',
}

function Row({ c }: { c: PreflightCheck }) {
  const Icon = c.ok ? CheckCircle2 : c.skipped ? CircleSlash : XCircle
  const color = c.ok ? 'text-ok' : c.skipped ? 'text-warn' : 'text-bad'
  return (
    <li className="flex items-start gap-2 text-sm" data-testid={`preflight-${c.check}`}>
      <Icon size={15} className={`mt-0.5 shrink-0 ${color}`} aria-label={c.ok ? 'passed' : c.skipped ? 'skipped' : 'failed'} />
      <div className="min-w-0">
        <span className="font-medium">{LABELS[c.check] ?? c.check}</span>
        <span className="ml-2 text-ink-400">{(c.ms / 1000).toFixed(1)}s</span>
        <div className="break-words text-xs text-ink-300">{c.detail}</div>
        {c.missingPermission && <div className="font-mono text-xs text-bad">missing permission: {c.missingPermission}</div>}
      </div>
    </li>
  )
}

/** Runs the agent's preflight for the chosen range. Read-only: never submits and never opens a locked part. */
export function PreflightPanel({ year, dayFrom, dayTo }: { year: number; dayFrom: number; dayTo: number }) {
  const [checks, setChecks] = useState<PreflightCheck[]>([])
  const [summary, setSummary] = useState<PreflightSummary | null>(null)
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)

  const run = async () => {
    setBusy(true)
    setErr(null)
    setSummary(null)
    setChecks([])
    try {
      setSummary(await preflight({ year, dayFrom, dayTo }, (c) => setChecks((prev) => [...prev, c])))
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="rounded-xl border border-ink-600 bg-ink-900/60 p-4">
      <div className="flex items-center justify-between gap-4">
        <div>
          <div className="font-semibold">Preflight</div>
          <div className="text-xs text-ink-400">Checks identity, storage, the AoC session, both models and the sandbox. Submits nothing.</div>
        </div>
        <Button variant="ghost" onClick={run} disabled={busy} className="shrink-0 whitespace-nowrap">
          {busy ? <Loader2 size={16} className="animate-spin" /> : <Stethoscope size={16} />} {busy ? 'Checking…' : 'Run preflight'}
        </Button>
      </div>
      {checks.length > 0 && (
        <ul className="mt-3 flex flex-col gap-2" aria-label="Preflight results">
          {checks.map((c) => (
            <Row key={c.check} c={c} />
          ))}
        </ul>
      )}
      {summary && (
        <p className={`mt-3 text-sm font-medium ${summary.ok ? 'text-ok' : 'text-bad'}`} role="status">
          {summary.ok ? `All checks passed (${summary.passed}).` : `Failed: ${summary.failed.map((f) => LABELS[f] ?? f).join(', ')}.`}
        </p>
      )}
      {err && <p className="mt-3 text-sm text-bad">{err}</p>}
    </div>
  )
}
