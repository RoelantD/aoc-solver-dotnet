import { Code2, Gauge, ListTree, MonitorPlay, Plus, Sparkles } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ActivityFeed } from '../components/ActivityFeed'
import { AgentStrip } from '../components/AgentStrip'
import { ApprovalCard } from '../components/ApprovalCard'
import { AccountBadge } from '../components/AuthGate'
import { ReplayBanner } from '../components/Banners'
import { Calendar, CalendarLegend } from '../components/Calendar'
import { CodeInspector } from '../components/CodeInspector'
import { MetricsPanel, RunControls, StatusHeader } from '../components/RunPanels'
import { StartRunDialog } from '../components/StartRunDialog'
import { Button, Panel } from '../components/ui'
import { useLatestRunId, useNow, useRun } from '../hooks/useRun'
import { buildDays, elapsedSeconds } from '../lib/runView'

export function Dashboard() {
  const { id } = useParams()
  const { runId, loading, error: listError } = useLatestRunId(id)
  const { snapshot, events, connected, lastHeartbeat, lastStar, codeVersion, error, control } = useRun(runId)
  const now = useNow()
  const [startOpen, setStartOpen] = useState(false)
  const [sel, setSel] = useState<{ day: number; part: number } | null>(null)
  const run = snapshot?.run
  const days = useMemo(() => (snapshot ? buildDays(snapshot, events) : []), [snapshot, events])
  const day = sel?.day ?? run?.currentDay ?? null
  const part = sel?.part ?? run?.currentPart ?? null
  const understanding = events.filter((e) => e.type === 'agent.analysis' && e.day === day && e.part === part).at(-1)

  return (
    <div className="flex min-h-full flex-col">
      <ReplayBanner run={run} />
      <header className="flex flex-wrap items-center gap-3 border-b border-ink-800 px-6 py-3">
        <Sparkles className="text-star" size={22} />
        <h1 className="text-lg font-semibold tracking-tight">Agentic Puzzle Solver</h1>
        <span className="text-sm text-ink-400">Spec-Driven · Microsoft Agent Framework · Foundry hosted agent · AG-UI</span>
        <nav className="ml-auto flex items-center gap-2">
          <Link to="/runs">
            <Button variant="ghost"><ListTree size={16} /> Runs</Button>
          </Link>
          {runId && (
            <Link to={`/present/${runId}`}>
              <Button variant="ghost"><MonitorPlay size={16} /> Presentation</Button>
            </Link>
          )}
          <Button variant="primary" onClick={() => setStartOpen(true)}>
            <Plus size={16} /> New live run
          </Button>
          <AccountBadge />
        </nav>
      </header>
      <StartRunDialog open={startOpen} onOpenChange={setStartOpen} />

      {!runId && !loading && (
        <div className="m-auto max-w-lg p-10 text-center text-ink-300">
          {listError ? (
            <p className="text-bad">Cannot reach the agent: {listError}</p>
          ) : (
            <>
              <p className="text-lg">No runs yet.</p>
              <p className="mt-2 text-sm text-ink-400">Start a live run to let the agent team solve Advent of Code autonomously.</p>
            </>
          )}
          <Button className="mt-6" variant="primary" onClick={() => setStartOpen(true)}><Plus size={16} /> New live run</Button>
        </div>
      )}

      {runId && !snapshot && (
        <div className="m-auto p-10 text-center text-ink-400">{error ? <span className="text-bad">{error}</span> : 'Connecting to the agent…'}</div>
      )}

      {runId && snapshot && run && (
        <main className="flex min-h-0 flex-1 flex-col gap-4 p-4 lg:p-6">
          <div className="rounded-2xl border border-ink-700 bg-ink-850/80 p-4">
            <StatusHeader
              snapshot={snapshot}
              days={days}
              events={events}
              elapsed={elapsedSeconds(run, now, events)}
              connected={connected}
              lastHeartbeat={lastHeartbeat}
            />
            <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
              <RunControls snapshot={snapshot} days={days} onControl={control} />
              <span className="text-xs text-ink-400">
                {run.id} · {run.approvalMode === 'auto' ? 'automatic submission authorized' : 'manual approval mode'}
                {error && <span className="ml-2 text-bad">{error}</span>}
              </span>
            </div>
          </div>
          <ApprovalCard approvals={snapshot.pendingApprovals} onDecide={(d, requestId) => control(d, { requestId })} />
          <AgentStrip activeAgent={snapshot.activeAgent} usage={snapshot.usage} events={events} running={run.status === 'running'} />
          <div className="grid min-h-0 flex-1 grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]">
            <div className="flex min-h-0 flex-col gap-4">
              <Panel title="Calendar" actions={<CalendarLegend />}>
                <div className="p-3">
                  <Calendar
                    days={days}
                    dayFrom={run.dayFrom}
                    dayTo={run.dayTo}
                    lastStar={lastStar}
                    currentDay={run.currentDay ?? null}
                    onSelect={(d, p) => setSel({ day: d, part: p })}
                  />
                </div>
              </Panel>
              <Panel title="Live activity" className="min-h-[22rem] flex-1">
                <ActivityFeed events={events} />
              </Panel>
            </div>
            <div className="flex min-h-0 flex-col gap-4">
              {understanding && (
                <Panel title={`Understanding · Day ${day} Part ${part}`} icon={<Sparkles size={14} />}>
                  <div className="space-y-1.5 p-4 text-sm">
                    <p>{understanding.data?.summary}</p>
                    <p className="text-ink-300"><span className="text-ink-400">Approach: </span>{understanding.data?.algorithm}</p>
                    <p className="font-mono text-xs text-ink-400">{understanding.data?.complexity} · {understanding.data?.model}</p>
                  </div>
                </Panel>
              )}
              <Panel title="Code inspector" icon={<Code2 size={14} />} className="min-h-[24rem] flex-1">
                <CodeInspector runId={runId} day={day} part={part} version={codeVersion} events={events} />
              </Panel>
              <Panel title="Inference metrics" icon={<Gauge size={14} />}>
                <MetricsPanel usage={snapshot.usage} events={events} />
              </Panel>
            </div>
          </div>
        </main>
      )}
    </div>
  )
}
