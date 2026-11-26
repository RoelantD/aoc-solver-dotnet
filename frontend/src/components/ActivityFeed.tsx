import {
  AlertTriangle,
  ArrowUpRight,
  Bot,
  CheckCircle2,
  Code2,
  CloudDownload,
  DatabaseBackup,
  FlaskConical,
  Info,
  Play,
  RotateCcw,
  Send,
  Sparkles,
  Star,
  XCircle,
} from 'lucide-react'
import { useEffect, useRef } from 'react'
import type { AgentEvent } from '../api/types'
import { fmtTime } from '../lib/format'
import { cx } from './ui'

// Too noisy for the feed. agent.escalation duplicates agent.handoff.
const HIDDEN = new Set(['part.state', 'model.call.finished', 'aoc.fetch', 'agent.escalation'])
const hidden = (e: AgentEvent) => HIDDEN.has(e.type) || (e.type === 'agent.active' && e.agent === 'system')

function iconFor(e: AgentEvent) {
  if (e.type === 'star.earned') return <Star size={16} className="text-star" fill="currentColor" />
  if (e.type === 'agent.escalation' || e.type === 'agent.handoff') return <ArrowUpRight size={16} className="text-opus" />
  if (e.type.startsWith('model.')) return <Bot size={16} className={e.agent === 'escalation-coder' || e.data?.model_key === 'escalation' ? 'text-opus' : 'text-sol'} />
  if (e.type === 'agent.code') return <Code2 size={16} className="text-sol" />
  if (e.type.startsWith('test.')) return <FlaskConical size={16} className={e.level === 'error' ? 'text-bad' : e.level === 'success' ? 'text-ok' : 'text-warn'} />
  if (e.type.startsWith('submit.')) return <Send size={16} className={e.level === 'success' ? 'text-ok' : e.level === 'error' ? 'text-bad' : 'text-warn'} />
  if (e.type.startsWith('aoc.')) return <CloudDownload size={16} className="text-ink-300" />
  if (e.type.startsWith('run.')) return <Play size={16} className="text-ink-300" />
  // Agent Framework workflow checkpoints: one per superstep, and the resume from the latest one.
  if (e.type === 'workflow.checkpoint') return <DatabaseBackup size={16} className="text-ink-400" />
  if (e.type === 'workflow.resumed' || e.type === 'workflow.restarted') return <RotateCcw size={16} className="text-ok" />
  if (e.type === 'agent.analysis') return <Sparkles size={16} className="text-sol" />
  if (e.level === 'error') return <XCircle size={16} className="text-bad" />
  if (e.level === 'warning') return <AlertTriangle size={16} className="text-warn" />
  if (e.level === 'success') return <CheckCircle2 size={16} className="text-ok" />
  return <Info size={16} className="text-ink-400" />
}

export function ActivityFeed({
  events,
  large = false,
  limit = 400,
}: {
  events: AgentEvent[]
  large?: boolean
  limit?: number
}) {
  const ref = useRef<HTMLOListElement>(null)
  const shown = events.filter((e) => !hidden(e)).slice(-limit)
  useEffect(() => {
    const el = ref.current
    if (el) el.scrollTop = el.scrollHeight
  }, [shown.length])
  if (!shown.length) {
    return <p className="p-4 text-sm text-ink-400">No activity yet. Events appear here as the agent works.</p>
  }
  // A "Day NN - Part N" header is inserted whenever the part changes between consecutive events.
  let lastKey = ''
  return (
    <ol ref={ref} className="scroll-thin min-h-0 flex-1 overflow-y-auto px-3 py-2" aria-live="polite" aria-label="Agent activity">
      {shown.map((e) => {
        const key = e.day ? `Day ${String(e.day).padStart(2, '0')} - Part ${e.part}` : ''
        const header = key && key !== lastKey ? key : ''
        if (key) lastKey = key
        return (
          <li key={e.seq} className="animate-slide-in">
            {header && (
              <div className={cx('mt-3 mb-1 font-mono font-semibold text-ink-300', large ? 'text-2xl' : 'text-xs')}>{header}</div>
            )}
            <div
              className={cx(
                'flex items-start gap-2.5 rounded-md px-2 py-1',
                e.type === 'star.earned' && 'bg-star/10',
                (e.type === 'agent.escalation' || e.type === 'agent.handoff') && 'bg-opus/10',
                large ? 'text-2xl leading-snug' : 'text-sm',
              )}
            >
              <span className="mt-0.5 shrink-0">{iconFor(e)}</span>
              <span
                className={cx(
                  'min-w-0 flex-1 break-words',
                  e.level === 'error' && 'text-bad',
                  e.level === 'success' && 'text-ok',
                  e.type === 'workflow.checkpoint' && 'text-ink-400',
                )}
              >
                {e.message}
              </span>
              {!large && <time className="shrink-0 font-mono text-[11px] text-ink-400">{fmtTime(e.ts)}</time>}
            </div>
          </li>
        )
      })}
    </ol>
  )
}
