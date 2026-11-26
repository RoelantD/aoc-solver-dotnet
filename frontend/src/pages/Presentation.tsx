import { Expand, LayoutDashboard, Shrink, Star } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ActivityFeed } from '../components/ActivityFeed'
import { AgentStrip } from '../components/AgentStrip'
import { ApprovalCard } from '../components/ApprovalCard'
import { ReplayBanner } from '../components/Banners'
import { Calendar } from '../components/Calendar'
import { CodeBlock, revisionResults, usePartDetail } from '../components/CodeInspector'
import { StatusPill } from '../components/RunPanels'
import { useLatestRunId, useNow, useRun } from '../hooks/useRun'
import { fmtDuration, fmtUsd } from '../lib/format'
import { buildDays, countStars, elapsedSeconds } from '../lib/runView'

// Event types skipped when picking the "latest step" headline.
const QUIET = ['part.state', 'aoc.fetch', 'model.call.finished', 'agent.escalation', 'agent.active']

export function Presentation() {
  const { id } = useParams()
  const { runId } = useLatestRunId(id)
  const { snapshot, events, connected, lastHeartbeat, lastStar, codeVersion, control } = useRun(runId)
  const now = useNow()
  const [fs, setFs] = useState(false)
  useEffect(() => {
    const h = () => setFs(!!document.fullscreenElement)
    document.addEventListener('fullscreenchange', h)
    return () => document.removeEventListener('fullscreenchange', h)
  }, [])
  const run = snapshot?.run
  const days = useMemo(() => (snapshot ? buildDays(snapshot, events) : []), [snapshot, events])
  const stars = countStars(days)
  const day = run?.currentDay ?? null
  const part = run?.currentPart ?? null
  const detail = usePartDetail(runId, day, part, codeVersion)
  const rev = detail?.revisions.at(-1)
  const lastExec = rev && day && part ? revisionResults(events, day, part, rev.revision).at(-1) : undefined
  const lastStep = [...events].reverse().find((e) => !QUIET.includes(e.type))

  if (!runId || !snapshot || !run) {
    return <div className="flex h-full items-center justify-center text-2xl text-ink-400">Waiting for a run…</div>
  }
  const execOk = lastExec?.type === 'test.result' ? lastExec.data?.passed === true : lastExec?.data?.status === 'ok'
  return (
    <div className="flex h-full flex-col">
      <ReplayBanner run={run} />
      <header className="flex items-center gap-6 px-8 pt-6">
        <div>
          <div className="text-sm font-semibold uppercase tracking-[0.25em] text-ink-400">Agentic Advent of Code · {run.year}</div>
          <div className="mt-1 flex items-center gap-4">
            <StatusPill status={run.status} connected={connected} lastHeartbeat={lastHeartbeat} />
            <span className="font-mono text-2xl text-ink-300">{fmtDuration(elapsedSeconds(run, now, events))}</span>
            <span className="font-mono text-xl text-ink-400">{fmtUsd(snapshot.usage.totals.costUsd)}</span>
          </div>
        </div>
        <div className="ml-auto flex items-center gap-8">
          <div className="text-right">
            <div className="flex items-center gap-3 text-[96px] font-bold leading-none text-star drop-shadow-[0_0_24px_rgba(251,191,36,0.45)]">
              <Star size={84} fill="currentColor" />
              <span className="tabular-nums">{stars.earnedThisRun}</span>
            </div>
            <div className="mt-1 text-base text-ink-400">stars verified by Advent of Code this run · +{stars.previouslyCompleted} earlier</div>
          </div>
          <div className="flex flex-col items-end gap-2">
            <div className="flex gap-2">
              <button
                className="rounded-lg border border-ink-600 p-2 text-ink-300 hover:bg-ink-700"
                aria-label={fs ? 'Exit fullscreen' : 'Enter fullscreen'}
                onClick={() => (fs ? document.exitFullscreen() : document.documentElement.requestFullscreen())}
              >
                {fs ? <Shrink size={20} /> : <Expand size={20} />}
              </button>
              <Link to={`/runs/${runId}`} className="rounded-lg border border-ink-600 p-2 text-ink-300 hover:bg-ink-700" aria-label="Dashboard">
                <LayoutDashboard size={20} />
              </Link>
            </div>
          </div>
        </div>
      </header>

      <main className="grid min-h-0 flex-1 grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)] gap-6 p-8 pt-5">
        <div className="flex min-h-0 flex-col gap-5">
          <Calendar days={days} dayFrom={run.dayFrom} dayTo={run.dayTo} lastStar={lastStar} currentDay={day} large />
          <AgentStrip
            activeAgent={snapshot.activeAgent}
            usage={snapshot.usage}
            events={events}
            running={run.status === 'running'}
            day={day}
            part={part}
            large
          />
        </div>
        <div className="flex min-h-0 flex-col gap-4">
          <ApprovalCard approvals={snapshot.pendingApprovals} onDecide={(d, requestId) => control(d, { requestId })} large />
          <div className="rounded-2xl border border-ink-700 bg-ink-850/80 p-5">
            <div className="text-3xl font-semibold">
              <span className="font-mono">{day ? `Day ${String(day).padStart(2, '0')} · Part ${part}` : '—'}</span>
            </div>
            {lastStep && <div className="mt-3 text-2xl leading-snug text-ink-100">{lastStep.message}</div>}
          </div>
          <div className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border border-ink-700 bg-ink-850/80">
            <ActivityFeed events={events} large limit={60} />
          </div>
          {rev && (
            <div className="rounded-2xl border border-ink-700 bg-ink-850/80 p-3">
              <div className="mb-2 flex items-center gap-3 px-1 text-base text-ink-300">
                <span className="font-mono">revision {rev.revision}</span>
                {lastExec && <span className={execOk ? 'text-ok' : 'text-bad'}>{lastExec.message}</span>}
              </div>
              <CodeBlock code={rev.code} maxHeight="15rem" fontSize={17} />
            </div>
          )}
        </div>
      </main>
    </div>
  )
}
