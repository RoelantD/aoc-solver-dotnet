import { useCallback, useEffect, useRef, useState } from 'react'
import {
  type AttachHandle,
  type CommandName,
  attach,
  command,
  loadLastSeq,
  loadTimeline,
  saveTimeline,
} from '../api/agent'
import type { AgentEvent, ListSnapshot, RunSnapshot, RunSummary } from '../api/types'

// Events that change what the code inspector shows; each one bumps `codeVersion` so the part detail is refetched.
const CODE_TYPES = new Set(['agent.code', 'test.result', 'exec.input'])
// Cap on the in-memory timeline; beyond it the oldest events are dropped.
const MAX_EVENTS = 3000

export interface RunView {
  snapshot: RunSnapshot | null
  events: AgentEvent[]
  connected: boolean
  lastHeartbeat: string | null
  /** The latest `star.earned` event, for the calendar's star animation. */
  lastStar: AgentEvent | null
  codeVersion: number
  error: string | null
  /** Runs a control command on this run; its snapshot replaces the state. `resume` re-attaches. */
  control: (name: CommandName, args?: Record<string, unknown>) => Promise<void>
}

/**
 * Live view of one run over AG-UI: the run snapshot kept current by deltas, a seq-deduplicated timeline of domain
 * events, and automatic reconnects (see `attach`). Bumping `generation` tears the stream down and attaches again.
 */
export function useRun(runId: string | null): RunView {
  const [snapshot, setSnapshot] = useState<RunSnapshot | null>(null)
  const [events, setEvents] = useState<AgentEvent[]>([])
  const [connected, setConnected] = useState(false)
  const [ended, setEnded] = useState(false)
  const [lastHeartbeat, setLastHeartbeat] = useState<string | null>(null)
  const [lastStar, setLastStar] = useState<AgentEvent | null>(null)
  const [codeVersion, setCodeVersion] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [generation, setGeneration] = useState(0)
  const resumeNext = useRef(false)
  const stream = useRef<AttachHandle | null>(null)

  // Load the cached timeline once per run, then (re)attach whenever `generation` changes.
  // (Effects run in declaration order, so the attach effect below sees the cache loaded here.)
  const cache = useRef<{ runId: string | null; events: AgentEvent[] }>({ runId: null, events: [] })

  useEffect(() => {
    cache.current = { runId, events: runId ? loadTimeline(runId) : [] }
    setSnapshot(null)
    setEvents(cache.current.events)
    setLastStar(null)
    setError(null)
    setEnded(false)
  }, [runId])

  useEffect(() => {
    if (!runId) return
    const cached = cache.current.events
    // Resume after the newest cached event; without a cache, ask for the full event log.
    const afterSeq = cached.length ? Math.min(loadLastSeq(runId), cached[cached.length - 1].seq) : 0
    const cmd = resumeNext.current ? 'resume' : 'attach'
    resumeNext.current = false
    setEnded(false)
    const handle = attach(
      runId,
      afterSeq,
      {
        onState: setSnapshot,
        onEvents: (added) => {
          setEvents((prev) => {
            const next = prev.length > MAX_EVENTS ? [...prev.slice(-2500), ...added] : [...prev, ...added]
            cache.current.events = next
            return next
          })
          const star = added.findLast((e) => e.type === 'star.earned')
          if (star) setLastStar(star)
          if (added.some((e) => CODE_TYPES.has(e.type))) setCodeVersion((v) => v + 1)
          setError(null)
        },
        onHeartbeat: setLastHeartbeat,
        onConnection: setConnected,
        onError: (e) => setError(e.message),
        onEnded: () => setEnded(true),
      },
      { command: cmd },
    )
    stream.current = handle
    return () => {
      handle.close()
      if (stream.current === handle) stream.current = null
    }
  }, [runId, generation])

  // Persist the timeline (debounced) so a reload resumes from the last seq instead of replaying everything.
  useEffect(() => {
    if (!runId || !events.length) return
    const t = setTimeout(() => saveTimeline(runId, events), 500)
    return () => clearTimeout(t)
  }, [runId, events])

  const control = useCallback(
    async (name: CommandName, args: Record<string, unknown> = {}) => {
      if (!runId) return
      if (name === 'resume') {
        // `resume` streams like attach: run it as the stream's own command.
        resumeNext.current = true
        setGeneration((g) => g + 1)
        return
      }
      const s = await command<RunSnapshot>(name, args, runId)
      setSnapshot(s)
      // A command such as `retry` can restart a finished run; its stream has ended, so attach again.
      if (ended && s.run.status === 'running') setGeneration((g) => g + 1)
    },
    [runId, ended],
  )

  return { snapshot, events, connected, lastHeartbeat, lastStar, codeVersion, error, control }
}

/** The run list (`list` command). */
export function useRunList(): { runs: RunSummary[] | null; error: string | null; reload: () => void } {
  const [runs, setRuns] = useState<RunSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [n, setN] = useState(0)
  useEffect(() => {
    let alive = true
    command<ListSnapshot>('list')
      .then((s) => {
        if (!alive) return
        setRuns([...s.runs].sort((a, b) => b.createdAt.localeCompare(a.createdAt)))
        setError(null)
      })
      .catch((e: Error) => alive && setError(e.message))
    return () => {
      alive = false
    }
  }, [n])
  return { runs, error, reload: () => setN((x) => x + 1) }
}

/** Route id, or the active live run, or the newest run. */
export function useLatestRunId(paramId: string | undefined): { runId: string | null; loading: boolean; error: string | null } {
  const { runs, error } = useRunList()
  if (paramId) return { runId: paramId, loading: false, error: null }
  if (!runs) return { runId: null, loading: !error, error }
  const live = runs.find((r) => r.mode === 'live' && (r.status === 'running' || r.status === 'created')) ?? runs.find((r) => r.mode === 'live') ?? runs[0]
  return { runId: live?.id ?? null, loading: false, error }
}

/** Current time, updated every `intervalMs` (for elapsed clocks). */
export function useNow(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(t)
  }, [intervalMs])
  return now
}
