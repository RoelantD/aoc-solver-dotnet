import { Activity, Bot, HeartPulse, Pause, Play, RotateCcw, Square, Star, Timer, Wallet, Wifi, WifiOff } from 'lucide-react'
import { useState } from 'react'
import type { CommandName } from '../api/agent'
import type { AgentEvent, DayInfo, RunSnapshot, RunUsage } from '../api/types'
import { fmtDuration, fmtTime, fmtTokens, fmtUsd } from '../lib/format'
import { agentLabel, agentModels, agentTone, countStars } from '../lib/runView'
import { Badge, Button, Stat, cx } from './ui'

const STATUS_TONE: Record<string, string> = {
  running: 'ok',
  paused: 'warn',
  created: 'neutral',
  stopped: 'neutral',
  completed: 'sol',
  budget_exhausted: 'bad',
  failed: 'bad',
}

export function StatusPill({ status, connected, lastHeartbeat }: { status: string; connected?: boolean; lastHeartbeat?: string | null }) {
  const live = status === 'running'
  return (
    <span className="inline-flex items-center gap-2">
      <Badge tone={STATUS_TONE[status] ?? 'neutral'} className="text-sm uppercase tracking-wider">
        {live && <span className="h-2 w-2 animate-pulse rounded-full bg-ok" />}
        {status.replace('_', ' ')}
      </Badge>
      {connected === undefined ? null : connected ? (
        <Wifi size={15} className="text-ok" aria-label="live stream connected" />
      ) : (
        <WifiOff size={15} className="text-bad" aria-label="live stream disconnected" />
      )}
      {lastHeartbeat && (
        <span className="inline-flex items-center gap-1 text-[11px] text-ink-400" title="last heartbeat from the agent">
          <HeartPulse size={12} /> {fmtTime(lastHeartbeat)}
        </span>
      )}
    </span>
  )
}

export function StatusHeader({
  snapshot,
  days,
  events,
  elapsed,
  connected,
  lastHeartbeat,
}: {
  snapshot: RunSnapshot
  days: DayInfo[]
  events: AgentEvent[]
  elapsed: number
  connected: boolean
  lastHeartbeat: string | null
}) {
  const r = snapshot.run
  const cur = r.currentDay ? days[r.currentDay - 1]?.parts[String(r.currentPart ?? 1)] : undefined
  const stars = countStars(days)
  const failed = days.flatMap((d) => Object.values(d.parts)).filter((p) => p.uiState === 'failed').length
  const blocked = days.flatMap((d) => Object.values(d.parts)).filter((p) => p.uiState === 'blocked').length
  const models = agentModels(events)
  const active = snapshot.activeAgent
  return (
    <div className="grid grid-cols-2 gap-x-6 gap-y-3 sm:grid-cols-4 xl:grid-cols-8">
      <Stat label="Calendar" value={`${r.year}`} sub={`days ${r.dayFrom}–${r.dayTo}`} />
      <div>
        <div className="text-[11px] font-semibold uppercase tracking-[0.14em] text-ink-400">Status</div>
        <div className="mt-1">
          <StatusPill status={r.status} connected={connected} lastHeartbeat={lastHeartbeat} />
        </div>
        {r.stopReason && r.status !== 'running' && <div className="mt-1 truncate text-xs text-ink-400" title={r.stopReason}>{r.stopReason}</div>}
      </div>
      <Stat
        label="Now solving"
        value={r.currentDay ? `Day ${String(r.currentDay).padStart(2, '0')} · P${r.currentPart}` : '—'}
        sub={cur ? cur.state.replaceAll('_', ' ').toLowerCase() : undefined}
      />
      <Stat
        label="Agent"
        value={<span className={agentTone(active)}>{active ? agentLabel(active) : '—'}</span>}
        sub={active ? models[active] : undefined}
      />
      <Stat
        label="Verified stars"
        tone="text-star"
        value={
          <span className="inline-flex items-center gap-1.5">
            <Star size={18} fill="currentColor" /> {stars.earnedThisRun}
          </span>
        }
        sub={`+${stars.previouslyCompleted} previously completed`}
      />
      <Stat label="Failed / blocked" value={`${failed} / ${blocked}`} tone={failed ? 'text-bad' : undefined} />
      <Stat label="Model calls" value={snapshot.usage.totals.calls} sub={`${fmtTokens(snapshot.usage.totals.inputTokens + snapshot.usage.totals.outputTokens)} tokens`} />
      <Stat label="Elapsed · cost" value={fmtDuration(elapsed)} sub={`${fmtUsd(snapshot.usage.totals.costUsd)} estimated`} />
    </div>
  )
}

/** Usage per model from the run snapshot (`usage.byModel`); model keys are named via the agents' `agent.active` events. */
export function MetricsPanel({ usage, events }: { usage: RunUsage | null | undefined; events: AgentEvent[] }) {
  if (!usage) return null
  const models = agentModels(events)
  const display: Record<string, string> = { workhorse: models.coder ?? 'Workhorse model', escalation: models['escalation-coder'] ?? 'Escalation model' }
  const rows = Object.entries(usage.byModel ?? {})
  const handoffs = events.filter((e) => e.type === 'agent.handoff').length
  return (
    <div className="p-3 text-sm">
      {rows.length === 0 && <p className="text-ink-400">No model calls yet.</p>}
      {rows.map(([k, m]) => (
        <div key={k} className="mb-3 rounded-lg border border-ink-700 bg-ink-900/50 p-3">
          <div className={cx('mb-2 flex items-center gap-2 font-semibold', k === 'escalation' ? 'text-opus' : 'text-sol')}>
            <Bot size={16} /> {display[k] ?? k}
          </div>
          <div className="grid grid-cols-3 gap-2">
            <Stat label="Calls" value={m.calls} />
            <Stat label="Tokens in/out" value={`${fmtTokens(m.inputTokens)} / ${fmtTokens(m.outputTokens)}`} sub={m.reasoningTokens ? `${fmtTokens(m.reasoningTokens)} reasoning` : undefined} />
            <Stat label="Avg latency · cost" value={`${(m.calls ? m.latencyMs / m.calls / 1000 : 0).toFixed(1)}s`} sub={fmtUsd(m.costUsd)} />
          </div>
        </div>
      ))}
      <div className="flex items-center gap-4 text-xs text-ink-400">
        <span className="inline-flex items-center gap-1"><Activity size={13} /> {usage.totals.calls} calls</span>
        <span className="inline-flex items-center gap-1"><Wallet size={13} /> {fmtUsd(usage.totals.costUsd)}</span>
        <span className="inline-flex items-center gap-1 text-opus"><Timer size={13} /> {handoffs} handoff(s)</span>
      </div>
    </div>
  )
}

export function RunControls({
  snapshot,
  days,
  onControl,
}: {
  snapshot: RunSnapshot
  days: DayInfo[]
  onControl: (name: CommandName, args?: Record<string, unknown>) => Promise<void>
}) {
  const r = snapshot.run
  const [busy, setBusy] = useState(false)
  const [err, setErr] = useState<string | null>(null)
  if (r.mode === 'replay') return null
  const act = async (name: CommandName, args?: Record<string, unknown>) => {
    setBusy(true)
    setErr(null)
    try {
      await onControl(name, args)
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  const retryable = days.flatMap((d) =>
    Object.entries(d.parts)
      .filter(([, p]) => ['failed', 'blocked', 'skipped'].includes(p.uiState))
      .map(([k]) => ({ day: d.day, part: Number(k) })),
  )
  return (
    <div className="flex flex-wrap items-center gap-2">
      {r.status === 'running' && (
        <Button disabled={busy} onClick={() => act('pause')}>
          <Pause size={16} /> Pause
        </Button>
      )}
      {(r.status === 'paused' || r.status === 'created') && (
        <Button variant="primary" disabled={busy} onClick={() => act('resume')}>
          <Play size={16} /> {r.status === 'created' ? 'Start' : 'Resume'}
        </Button>
      )}
      {['running', 'paused', 'created'].includes(r.status) && (
        <Button variant="danger" disabled={busy} onClick={() => act('stop')}>
          <Square size={14} /> Stop
        </Button>
      )}
      {r.status !== 'running' &&
        retryable.slice(0, 3).map((p) => (
          <Button key={`r${p.day}-${p.part}`} variant="ghost" disabled={busy} onClick={() => act('retry', { day: p.day, part: p.part })}>
            <RotateCcw size={14} /> Retry D{p.day} P{p.part}
          </Button>
        ))}
      {err && <span className="text-sm text-bad">{err}</span>}
    </div>
  )
}
