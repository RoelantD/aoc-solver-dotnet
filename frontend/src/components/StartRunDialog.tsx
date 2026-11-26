import { Rocket, ShieldCheck } from 'lucide-react'
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { command, newId } from '../api/agent'
import type { RunSnapshot, StartArgs } from '../api/types'
import { PreflightPanel } from './PreflightPanel'
import { Button, Dialog, Switch } from './ui'

function Field({ label, children, hint }: { label: string; children: React.ReactNode; hint?: string }) {
  return (
    <label className="flex flex-col gap-1 text-sm">
      <span className="text-ink-300">{label}</span>
      {children}
      {hint && <span className="text-xs text-ink-400">{hint}</span>}
    </label>
  )
}

const input = 'rounded-lg border border-ink-600 bg-ink-900 px-3 py-2 font-mono text-sm focus:border-sol focus:outline-none'

/** The `start` command's arguments from the dialog state (RunStreamAgent.CreateRunAsync). */
export function startArgs(v: {
  year: number
  from: number
  to: number
  authorized: boolean
  manual: boolean
  attempts: number
  escalateAfter: number
  spend: number
  timeout: number
}): StartArgs {
  return {
    year: v.year,
    dayFrom: v.from,
    dayTo: v.to,
    authorizeSubmissions: v.authorized,
    // Without authorization the agent always uses manual approval.
    approvalMode: v.authorized && !v.manual ? 'auto' : 'manual',
    limits: { maxAttemptsPerPart: v.attempts, escalateAfterFailures: v.escalateAfter, maxSpendUsd: v.spend, execTimeoutS: v.timeout },
  }
}

export function StartRunDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (v: boolean) => void }) {
  const nav = useNavigate()
  const [year, setYear] = useState(2024)
  const [from, setFrom] = useState(1)
  const [to, setTo] = useState(25)
  const [autoSubmit, setAutoSubmit] = useState(true)
  const [authorized, setAuthorized] = useState(false)
  const [attempts, setAttempts] = useState(6)
  const [escalateAfter, setEscalateAfter] = useState(3)
  const [spend, setSpend] = useState(20)
  const [timeout, setTimeoutS] = useState(30)
  const [err, setErr] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const canStart = !busy && (!autoSubmit || authorized)

  const submit = async () => {
    setBusy(true)
    setErr(null)
    try {
      const args = startArgs({ year, from, to, authorized: autoSubmit && authorized, manual: !autoSubmit, attempts, escalateAfter, spend, timeout })
      // `start` streams like attach; the first snapshot names the new run and the dashboard then attaches to it.
      const snapshot = await command<RunSnapshot>('start', args as unknown as Record<string, unknown>, newId())
      onOpenChange(false)
      nav(`/runs/${snapshot.run.id}`)
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Start a live run" description="Solves real puzzles with real models in the sandbox and submits to the real website.">
      <div className="flex flex-col gap-4">
        <div className="grid grid-cols-3 gap-3">
          <Field label="Year" hint="Past events only">
            <input className={input} type="number" min={2015} max={2100} value={year} onChange={(e) => setYear(+e.target.value)} aria-label="Year" />
          </Field>
          <Field label="First day">
            <input className={input} type="number" min={1} max={25} value={from} onChange={(e) => setFrom(+e.target.value)} />
          </Field>
          <Field label="Last day">
            <input className={input} type="number" min={1} max={25} value={to} onChange={(e) => setTo(+e.target.value)} />
          </Field>
        </div>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Attempts per part">
            <input className={input} type="number" min={1} max={20} value={attempts} onChange={(e) => setAttempts(+e.target.value)} />
          </Field>
          <Field label="Escalate after failures" hint="0 disables escalation">
            <input className={input} type="number" min={0} max={20} value={escalateAfter} onChange={(e) => setEscalateAfter(+e.target.value)} />
          </Field>
          <Field label="Max spend (USD)">
            <input className={input} type="number" min={0} step={1} value={spend} onChange={(e) => setSpend(+e.target.value)} />
          </Field>
          <Field label="Execution timeout (s)">
            <input className={input} type="number" min={5} max={220} value={timeout} onChange={(e) => setTimeoutS(+e.target.value)} />
          </Field>
        </div>

        <div className="rounded-xl border border-ink-600 bg-ink-900/60 p-4">
          <div className="flex items-center justify-between gap-4">
            <div>
              <div className="font-semibold">Automatic answer submission</div>
              <div className="text-xs text-ink-400">Submits candidate answers to adventofcode.com without asking per answer.</div>
            </div>
            <Switch id="auto-submit" label="Automatic answer submission" checked={autoSubmit} onCheckedChange={setAutoSubmit} />
          </div>
          {autoSubmit ? (
            <label className="mt-3 flex cursor-pointer items-start gap-3 rounded-lg border border-warn/40 bg-warn/10 p-3 text-sm">
              <input type="checkbox" className="mt-0.5 h-4 w-4 accent-[#fb923c]" checked={authorized} onChange={(e) => setAuthorized(e.target.checked)} />
              <span>
                <ShieldCheck size={15} className="mr-1 inline text-warn" />I authorize this application to submit answers to Advent of Code on my behalf for this
                entire run. Each distinct answer is submitted at most once; server cooldowns are respected.
              </span>
            </label>
          ) : (
            <p className="mt-3 rounded-lg border border-ink-600 bg-ink-800 p-3 text-sm text-ink-300">
              Manual-approval mode: each candidate answer waits for your approval in the dashboard. Without submission, Advent
              of Code will not unlock Part 2, so progression stops at every Part 1 until you approve.
            </p>
          )}
        </div>

        <PreflightPanel year={year} dayFrom={from} dayTo={to} />

        {err && <p className="text-sm text-bad">{err}</p>}
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button variant="primary" disabled={!canStart} onClick={submit}>
            <Rocket size={16} /> {busy ? 'Starting…' : 'Start live run'}
          </Button>
        </div>
      </div>
    </Dialog>
  )
}
