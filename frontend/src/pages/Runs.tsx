import { ArrowLeft, History, Plus, RefreshCw } from 'lucide-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { AccountBadge } from '../components/AuthGate'
import { StatusPill } from '../components/RunPanels'
import { StartRunDialog } from '../components/StartRunDialog'
import { Badge, Button, Panel } from '../components/ui'
import { useNow, useRunList } from '../hooks/useRun'
import { AGENT_URL, authEnabled } from '../lib/auth'
import { fmtDuration, fmtUsd } from '../lib/format'
import { elapsedSeconds } from '../lib/runView'

export function Runs() {
  const { runs, error, reload } = useRunList()
  const [open, setOpen] = useState(false)
  const nav = useNavigate()
  const now = useNow()
  return (
    <div className="mx-auto flex max-w-6xl flex-col gap-4 p-6">
      <div className="flex items-center gap-3">
        <Link to="/"><Button variant="ghost"><ArrowLeft size={16} /> Dashboard</Button></Link>
        <h1 className="text-xl font-semibold">Runs</h1>
        <span className="ml-auto" />
        <AccountBadge />
        <Button variant="ghost" aria-label="Reload runs" onClick={reload}><RefreshCw size={16} /></Button>
        <Button variant="primary" onClick={() => setOpen(true)}><Plus size={16} /> New live run</Button>
      </div>
      <StartRunDialog open={open} onOpenChange={setOpen} />
      <Panel title="Agent">
        <div className="space-y-1 p-4 text-sm text-ink-300">
          <div>
            Endpoint: <span className="break-all font-mono text-xs">{AGENT_URL}</span>{' '}
            {authEnabled ? <Badge tone="sol">hosted · Entra sign-in</Badge> : <Badge tone="neutral">local · no sign-in</Badge>}
          </div>
          {error && <div className="text-bad">Cannot reach the agent: {error}</div>}
        </div>
      </Panel>
      <Panel title="Previous runs">
        <table className="w-full text-left text-sm">
          <thead className="text-xs uppercase tracking-wider text-ink-400">
            <tr className="border-b border-ink-700">
              <th className="px-4 py-2">Run</th><th>Mode</th><th>Year · days</th><th>Status</th><th>Approval</th><th>Elapsed</th><th>Cost</th><th>Created</th>
            </tr>
          </thead>
          <tbody>
            {(runs ?? []).map((r) => (
              <tr key={r.id} className="cursor-pointer border-b border-ink-800 hover:bg-ink-800/60" onClick={() => nav(`/runs/${r.id}`)}>
                <td className="px-4 py-2 font-mono text-xs">{r.id}</td>
                <td>{r.mode === 'replay' ? <Badge tone="warn"><History size={12} /> replay</Badge> : <Badge tone="sol">live</Badge>}</td>
                <td>{r.year} · {r.dayFrom}–{r.dayTo}</td>
                <td><StatusPill status={r.status} /></td>
                <td>{r.approvalMode}</td>
                <td className="font-mono">{fmtDuration(elapsedSeconds(r, now, []))}</td>
                <td className="font-mono">{fmtUsd(r.costUsd)}</td>
                <td className="text-ink-400">{new Date(r.createdAt).toLocaleString()}</td>
              </tr>
            ))}
            {runs && !runs.length && (
              <tr><td colSpan={8} className="px-4 py-6 text-center text-ink-400">No runs recorded yet.</td></tr>
            )}
            {!runs && !error && (
              <tr><td colSpan={8} className="px-4 py-6 text-center text-ink-400">Loading…</td></tr>
            )}
          </tbody>
        </table>
      </Panel>
    </div>
  )
}
