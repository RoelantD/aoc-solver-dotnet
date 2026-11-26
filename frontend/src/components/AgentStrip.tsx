import { ArrowRight, Bot, Cpu, Zap } from 'lucide-react'
import type { AgentEvent, RunUsage } from '../api/types'
import { fmtTokens, fmtUsd } from '../lib/format'
import { agentLabel, agentModels, agentTone, latestHandoff } from '../lib/runView'
import { cx } from './ui'

const TEAM = ['analyst', 'coder', 'escalation-coder'] as const

/**
 * The multi-agent strip: which agent is working (with its model), the latest Coder -> Escalation Coder handoff
 * with its reason, and model usage per agent from the snapshot's `usage.byAgent`.
 */
export function AgentStrip({
  activeAgent,
  usage,
  events,
  running = false,
  day,
  part,
  large = false,
}: {
  activeAgent?: string | null
  usage?: RunUsage | null
  events: AgentEvent[]
  running?: boolean
  day?: number | null
  part?: number | null
  large?: boolean
}) {
  const models = agentModels(events)
  const handoff = latestHandoff(events, day, part)
  return (
    <div
      className={cx('flex flex-col gap-3 rounded-2xl border border-ink-700 bg-ink-850/80', large ? 'p-5' : 'p-3')}
      aria-label="Agent team"
      role="group"
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className={cx('font-semibold uppercase tracking-[0.14em] text-ink-400', large ? 'text-sm' : 'text-[11px]')}>Active agent</span>
        <span
          data-testid="active-agent"
          className={cx('inline-flex items-center gap-2 font-semibold', agentTone(activeAgent), large ? 'text-2xl' : 'text-base')}
        >
          {activeAgent && running && <span className="h-2 w-2 animate-pulse rounded-full bg-ok" />}
          {activeAgent === 'system' ? <Cpu size={large ? 24 : 16} /> : <Bot size={large ? 24 : 16} />}
          {activeAgent ? agentLabel(activeAgent) : 'Idle'}
          {activeAgent && models[activeAgent] && <span className="font-normal text-ink-300">· {models[activeAgent]}</span>}
        </span>
      </div>

      {handoff && (
        <div
          role="status"
          aria-label="Agent handoff"
          className={cx(
            'animate-slide-in flex items-center gap-3 rounded-xl border border-opus/50 bg-gradient-to-r from-sol/10 via-opus/15 to-opus/5 px-3',
            large ? 'py-3 text-xl' : 'py-2 text-sm',
          )}
        >
          <Zap className="shrink-0 text-opus" size={large ? 24 : 16} />
          <span className="font-semibold text-sol">{agentLabel(handoff.data?.from ?? 'coder')}</span>
          <ArrowRight className="shrink-0 text-ink-300" size={large ? 22 : 16} />
          <span className="font-semibold text-opus">{agentLabel(handoff.data?.to ?? 'escalation-coder')}</span>
          <span className="min-w-0 truncate text-ink-300" title={handoff.data?.reason}>— {handoff.data?.reason}</span>
        </div>
      )}

      <div className={cx('grid gap-2', large ? 'grid-cols-3' : 'grid-cols-1 sm:grid-cols-3')}>
        {TEAM.map((id) => {
          const u = usage?.byAgent?.[id]
          const active = activeAgent === id
          return (
            <div
              key={id}
              data-testid={`agent-${id}`}
              className={cx(
                'rounded-lg border px-3 py-2',
                active ? 'border-sol/70 bg-sol/10' : 'border-ink-700 bg-ink-900/50',
                active && id === 'escalation-coder' && 'border-opus/70 bg-opus/10',
              )}
            >
              <div className={cx('flex items-center gap-1.5 font-semibold', agentTone(id), large ? 'text-lg' : 'text-sm')}>
                <Bot size={large ? 18 : 14} /> {agentLabel(id)}
              </div>
              <div className={cx('truncate text-ink-400', large ? 'text-sm' : 'text-xs')}>{models[id] ?? '—'}</div>
              <div className={cx('mt-1 font-mono text-ink-300', large ? 'text-base' : 'text-xs')}>
                {u ? `${u.calls} calls · ${fmtTokens(u.inputTokens + u.outputTokens)} tok · ${fmtUsd(u.costUsd)}` : 'no calls yet'}
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
