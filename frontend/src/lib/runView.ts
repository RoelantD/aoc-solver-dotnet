// View-model helpers: turn the AG-UI run snapshot and the CUSTOM-event timeline into what the components render.
import type { AgentEvent, DayInfo, PartInfo, PartState, RunSnapshot, RunSummary, UiState } from '../api/types'

const AGENT_LABEL: Record<string, string> = {
  analyst: 'Analyst',
  coder: 'Coder',
  'escalation-coder': 'Escalation Coder',
  system: 'System',
}

export function agentLabel(id: string | null | undefined): string {
  if (!id) return '—'
  return AGENT_LABEL[id] ?? id
}

/** Accent colour per agent: the escalation coder (Claude) is purple, the workhorse agents (GPT) are blue. */
export function agentTone(id: string | null | undefined): string {
  if (id === 'escalation-coder') return 'text-opus'
  if (id === 'coder' || id === 'analyst') return 'text-sol'
  return 'text-ink-300'
}

const SOLVING = new Set<PartState>(['FETCHING', 'ANALYZING', 'GENERATING', 'TESTING', 'REPAIRING', 'ESCALATING', 'EXECUTING_INPUT'])

export function toUiState(state: PartState, previouslyCompleted = false): UiState {
  if (state === 'VERIFIED') return previouslyCompleted ? 'previously_completed' : 'verified'
  if (SOLVING.has(state)) return 'solving'
  switch (state) {
    case 'READY_TO_SUBMIT':
    case 'WAITING_FOR_APPROVAL':
      return 'tests_passed'
    case 'SUBMITTING':
    case 'WAITING_FOR_COOLDOWN':
      return 'submitted'
    case 'FAILED':
      return 'failed'
    case 'BLOCKED':
      return 'blocked'
    case 'SKIPPED':
      return 'skipped'
    default:
      return 'unsolved'
  }
}

const key = (day: number, part: number) => `${day}-${part}`

/** Calendar view model: part states from the snapshot, escalation / reasons / prior completion from the timeline. */
export function buildDays(snapshot: RunSnapshot, events: AgentEvent[]): DayInfo[] {
  const previously = new Set<string>()
  const escalated = new Set<string>()
  const reasons = new Map<string, string>()
  for (const e of events) {
    if (e.day == null || e.part == null) continue
    const k = key(e.day, e.part)
    if (e.type === 'aoc.previously_completed') previously.add(k)
    else if (e.type === 'agent.handoff' || e.type === 'agent.escalation') escalated.add(k)
    else if (e.type === 'part.retry') escalated.delete(k)
    if (e.type === 'part.state' && typeof e.data?.reason === 'string' && e.data.reason) reasons.set(k, e.data.reason)
    if (e.type === 'part.failed' || e.type === 'part.skipped') reasons.set(k, e.message)
  }
  const awaiting = new Set(snapshot.pendingApprovals.filter((a) => a.state === 'pending').map((a) => key(a.day, a.part)))
  return snapshot.calendar.map((parts, i) => {
    const day = i + 1
    const info: Record<string, PartInfo> = {}
    parts.forEach((state, j) => {
      const k = key(day, j + 1)
      info[String(j + 1)] = {
        state,
        uiState: toUiState(state, previously.has(k)),
        escalated: escalated.has(k),
        statusReason: reasons.get(k) ?? null,
        awaitingApproval: awaiting.has(k) || state === 'WAITING_FOR_APPROVAL',
      }
    })
    return { day, title: snapshot.titles?.[i] ?? null, parts: info }
  })
}

export interface StarCounts {
  total: number
  earnedThisRun: number
  previouslyCompleted: number
}

/** Stars are counted from parts Advent of Code confirmed (VERIFIED) only. */
export function countStars(days: DayInfo[]): StarCounts {
  let earned = 0
  let previously = 0
  for (const d of days)
    for (const p of Object.values(d.parts)) {
      if (p.uiState === 'verified') earned++
      else if (p.uiState === 'previously_completed') previously++
    }
  return { total: earned + previously, earnedThisRun: earned, previouslyCompleted: previously }
}

/** Elapsed run time from the summary's timestamps; a paused run counts up to its latest event. */
export function elapsedSeconds(run: RunSummary, now: number, events: AgentEvent[]): number {
  if (!run.startedAt) return 0
  const start = new Date(run.startedAt).getTime()
  let end = now
  if (run.finishedAt) end = new Date(run.finishedAt).getTime()
  else if (run.status !== 'running') {
    const last = events.at(-1)
    end = last ? new Date(last.ts).getTime() : start
  }
  return Math.max(0, (end - start) / 1000)
}

/** Latest Coder -> Escalation Coder handoff, optionally for one part. */
export function latestHandoff(events: AgentEvent[], day?: number | null, part?: number | null): AgentEvent | null {
  for (let i = events.length - 1; i >= 0; i--) {
    const e = events[i]
    if (e.type !== 'agent.handoff') continue
    if (day != null && part != null && (e.day !== day || e.part !== part)) continue
    return e
  }
  return null
}

/** Model display name per agent id, from the latest `agent.active` (data {agent, model}) and `agent.analysis` events. */
export function agentModels(events: AgentEvent[]): Record<string, string> {
  const models: Record<string, string> = {}
  for (const e of events) {
    if (e.type === 'agent.active' && typeof e.data?.agent === 'string' && typeof e.data?.model === 'string') models[e.data.agent] = e.data.model
    else if (e.type === 'agent.analysis' && typeof e.data?.model === 'string') models.analyst = e.data.model
  }
  return models
}
