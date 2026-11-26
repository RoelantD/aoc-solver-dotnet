// AG-UI client for the .NET solver agent.
//
// Every request is a POST of an AG-UI RunAgentInput to the agent's Invocations URL. The dashboard never chats with
// the agent: the command travels in forwardedProps ({command, args, afterSeq}) and messages stay empty, so no
// operator input ever reaches a model. The response is an SSE stream of AG-UI events, parsed by @ag-ui/client's
// HttpAgent:
//   STATE_SNAPSHOT  -> replaces the shared state (run snapshot, run list, part detail or preflight summary)
//   STATE_DELTA     -> RFC 6902 JSON Patch against the last run snapshot
//   CUSTOM          -> a domain event: name = event type, value = {seq, ts, level, day, part, agent, message, data}
//   CUSTOM heartbeat (seq 0) keeps idle streams alive; CUSTOM command.error and RUN_ERROR report a refused command.
//
// Approvals are ordinary commands too: `approve` / `reject` with {requestId} store the operator's decision, and the
// run applies it.
import { type BaseEvent, EventType, HttpAgent, type RunAgentInput } from '@ag-ui/client'
import { AGENT_URL, getToken, isLocalAgentUrl } from '../lib/auth'
import { type PatchOperation, applyPatch } from '../lib/jsonPatch'
import type { AgentEvent, RunSnapshot } from './types'

export type CommandName =
  | 'start'
  | 'attach'
  | 'pause'
  | 'resume'
  | 'stop'
  | 'approve'
  | 'reject'
  | 'retry'
  | 'list'
  | 'detail'
  | 'preflight'
  | 'replay'

/** A command the agent refused (CUSTOM `command.error` or RUN_ERROR). `code` is e.g. `run_active`, `not_found`. */
export class CommandError extends Error {
  code: string
  constructor(message: string, code = 'internal') {
    super(message)
    this.code = code
  }
}

// ---------------------------------------------------------------- transport (overridable in tests)

export interface Transport {
  url: string
  fetch: (url: string, init: RequestInit) => Promise<Response>
  getToken: () => Promise<string | null>
}

const transport: Transport = {
  url: AGENT_URL,
  fetch: (url, init) => globalThis.fetch(url, init),
  getToken,
}

/** Test hook: replaces parts of the transport and returns a function restoring the previous one. */
export function setTransport(partial: Partial<Transport>): () => void {
  const previous = { ...transport }
  Object.assign(transport, partial)
  return () => Object.assign(transport, previous)
}

const SESSION_HEADER = 'x-agent-session-id'
const SESSION_KEY = 'aps.agentSessionId'

function storageGet(key: string): string | null {
  try {
    return sessionStorage.getItem(key)
  } catch {
    return null
  }
}

function storageSet(key: string, value: string): void {
  try {
    sessionStorage.setItem(key, value)
  } catch {
    /* storage unavailable: affinity and resume are preferences only */
  }
}

/**
 * The request URL. A Foundry hosted agent answers with an `x-agent-session-id` header; sending it back as
 * `agent_session_id` routes later requests to the same session, which keeps the run's state warm.
 */
export function requestUrl(base: string = transport.url): string {
  if (isLocalAgentUrl(base)) return base
  const session = storageGet(SESSION_KEY)
  if (!session) return base
  const url = new URL(base)
  url.searchParams.set('agent_session_id', session)
  return url.toString()
}

export function newId(): string {
  const c = globalThis.crypto
  if (c && typeof c.randomUUID === 'function') return c.randomUUID()
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (ch) => {
    const r = (Math.random() * 16) | 0
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16)
  })
}

/** The AG-UI RunAgentInput carrying a command. For a run, threadId is the run id; afterSeq resumes its event log. */
export function buildInput(name: CommandName, args: Record<string, unknown> = {}, threadId: string = newId(), afterSeq = 0): RunAgentInput {
  return {
    threadId,
    runId: newId(),
    messages: [],
    state: {},
    tools: [],
    context: [],
    forwardedProps: { command: name, args, afterSeq },
  }
}

interface StreamObserver {
  next: (event: BaseEvent) => void
  error: (error: unknown) => void
  complete: () => void
}

interface StreamHandle {
  close: () => void
}

/**
 * Sends one request and streams its AG-UI events to `observer`. The bearer token is fetched per request so MSAL can
 * refresh it. `close()` drops the connection only; the run keeps going server side.
 */
function openStream(input: RunAgentInput, observer: StreamObserver): StreamHandle {
  let closed = false
  let agent: HttpAgent | null = null
  let unsubscribe: (() => void) | null = null
  void (async () => {
    let token: string | null
    try {
      token = await transport.getToken()
    } catch (e) {
      if (!closed) observer.error(e)
      return
    }
    if (closed) return
    const headers: Record<string, string> = {}
    if (token) headers.Authorization = `Bearer ${token}`
    agent = new HttpAgent({
      url: requestUrl(),
      headers,
      fetch: async (url, init) => {
        const res = await transport.fetch(url, init)
        const session = res.headers.get(SESSION_HEADER)
        if (session) storageSet(SESSION_KEY, session)
        if (!res.ok) {
          let detail = res.statusText
          try {
            detail = (await res.text()) || detail
          } catch {
            /* ignore */
          }
          throw new CommandError(`agent returned HTTP ${res.status}: ${detail.slice(0, 300)}`, res.status === 401 || res.status === 403 ? 'unauthorized' : 'http')
        }
        return res
      },
    })
    const sub = agent.run(input).subscribe({
      next: (e) => !closed && observer.next(e),
      error: (e) => !closed && observer.error(e),
      complete: () => !closed && observer.complete(),
    })
    unsubscribe = () => sub.unsubscribe()
  })()
  return {
    close: () => {
      if (closed) return
      closed = true
      unsubscribe?.()
      try {
        agent?.abortRun()
      } catch {
        /* already finished */
      }
    },
  }
}

interface CustomValue {
  seq?: number
  ts?: string
  level?: AgentEvent['level']
  day?: number | null
  part?: number | null
  agent?: string | null
  message?: string
  data?: Record<string, unknown> | null
  replay?: boolean
}

function customOf(event: BaseEvent): { name: string; value: CustomValue } {
  const e = event as BaseEvent & { name?: string; value?: unknown }
  return { name: e.name ?? '', value: (e.value ?? {}) as CustomValue }
}

function commandErrorOf(event: BaseEvent): CommandError | null {
  if (event.type === EventType.RUN_ERROR) {
    const e = event as BaseEvent & { message?: string; code?: string }
    return new CommandError(e.message ?? 'the agent reported an error', e.code ?? 'internal')
  }
  if (event.type === EventType.CUSTOM) {
    const { name, value } = customOf(event)
    if (name === 'command.error') {
      const code = typeof value.data?.code === 'string' ? value.data.code : 'internal'
      return new CommandError(value.message ?? 'command failed', code)
    }
  }
  return null
}

/**
 * Sends a command and resolves with the first STATE_SNAPSHOT, or rejects with the command error.
 * For streaming commands (start, resume) the stream is dropped after the snapshot; the run keeps going server side.
 */
export function command<T = unknown>(name: CommandName, args: Record<string, unknown> = {}, threadId?: string): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    let settled = false
    const settle = (fn: () => void) => {
      if (settled) return
      settled = true
      fn()
      handle.close()
    }
    const handle = openStream(buildInput(name, args, threadId ?? newId()), {
      next: (event) => {
        const err = commandErrorOf(event)
        if (err) settle(() => reject(err))
        else if (event.type === EventType.STATE_SNAPSHOT)
          settle(() => resolve((event as BaseEvent & { snapshot: T }).snapshot))
      },
      error: (e) => settle(() => reject(e instanceof Error ? e : new Error(String(e)))),
      complete: () => settle(() => reject(new CommandError(`command '${name}' finished without a snapshot`))),
    })
  })
}

// ---------------------------------------------------------------- preflight

/** One `preflight.check` result (PreflightService). */
export interface PreflightCheck {
  check: string
  ok: boolean
  detail: string
  ms: number
  missingPermission?: string | null
  skipped?: boolean
}

export interface PreflightSummary {
  ok: boolean
  year: number
  dayFrom: number
  dayTo: number
  passed: number
  failed: string[]
  skipped: string[]
  checks: PreflightCheck[]
}

/** Runs `preflight`, reporting each check as it completes; resolves with the summary snapshot. Never submits. */
export function preflight(args: { year?: number; dayFrom?: number; dayTo?: number }, onCheck: (c: PreflightCheck) => void): Promise<PreflightSummary> {
  return new Promise<PreflightSummary>((resolve, reject) => {
    let settled = false
    const settle = (fn: () => void) => {
      if (settled) return
      settled = true
      fn()
      handle.close()
    }
    const handle = openStream(buildInput('preflight', args), {
      next: (event) => {
        const err = commandErrorOf(event)
        if (err) return settle(() => reject(err))
        if (event.type === EventType.CUSTOM) {
          const { name, value } = customOf(event)
          if (name === 'preflight.check' && value.data) onCheck(value.data as unknown as PreflightCheck)
        } else if (event.type === EventType.STATE_SNAPSHOT) {
          const snap = (event as BaseEvent & { snapshot: { preflight?: PreflightSummary } }).snapshot
          if (snap.preflight) settle(() => resolve(snap.preflight!))
        }
      },
      error: (e) => settle(() => reject(e instanceof Error ? e : new Error(String(e)))),
      complete: () => settle(() => reject(new CommandError('preflight finished without a summary'))),
    })
  })
}

// ---------------------------------------------------------------- run stream (attach)

// The last seen seq and the timeline are cached per run in sessionStorage, so a page reload resumes the stream
// from where it was instead of replaying the whole event log.

const seqKey = (runId: string) => `aps.lastSeq.${runId}`
const timelineKey = (runId: string) => `aps.timeline.${runId}`

export function loadLastSeq(runId: string): number {
  const v = Number(storageGet(seqKey(runId)))
  return Number.isFinite(v) && v > 0 ? v : 0
}

function saveLastSeq(runId: string, seq: number): void {
  storageSet(seqKey(runId), String(seq))
}

export function loadTimeline(runId: string): AgentEvent[] {
  try {
    const raw = storageGet(timelineKey(runId))
    const parsed = raw ? (JSON.parse(raw) as AgentEvent[]) : []
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

export function saveTimeline(runId: string, events: AgentEvent[]): void {
  storageSet(timelineKey(runId), JSON.stringify(events.slice(-1500)))
}

/** What one AG-UI event changed in a run stream. */
export interface StreamUpdate {
  state?: RunSnapshot
  events?: AgentEvent[]
  heartbeat?: string
  error?: CommandError
  /** A delta could not be applied: reconnect to get a fresh snapshot. */
  resync?: boolean
}

/**
 * Folds AG-UI events into the run state and a seq-deduplicated timeline.
 *
 * The agent sends each domain event as a CUSTOM event followed by the STATE_DELTA it caused. After a reconnect the
 * stream starts with a fresh snapshot and may repeat events at or below the last seen seq; those are dropped, and
 * so is the delta right after each one, because the snapshot already includes it. A delta that does not apply
 * means the local state drifted: the caller reconnects to get a new snapshot.
 */
export class RunStreamState {
  snapshot: RunSnapshot | null = null
  lastSeq: number
  lastHeartbeat: string | null = null
  private skipDelta = false
  readonly runId: string

  constructor(runId: string, afterSeq = 0) {
    this.runId = runId
    this.lastSeq = afterSeq
  }

  handle(event: BaseEvent): StreamUpdate {
    const err = commandErrorOf(event)
    if (err) return { error: err }
    switch (event.type) {
      case EventType.STATE_SNAPSHOT: {
        const snapshot = (event as BaseEvent & { snapshot: RunSnapshot }).snapshot
        this.snapshot = snapshot
        this.skipDelta = false
        return { state: snapshot }
      }
      case EventType.STATE_DELTA: {
        if (this.skipDelta || !this.snapshot) return {}
        try {
          this.snapshot = applyPatch(this.snapshot, (event as BaseEvent & { delta: PatchOperation[] }).delta)
          return { state: this.snapshot }
        } catch {
          return { resync: true }
        }
      }
      case EventType.CUSTOM: {
        const { name, value } = customOf(event)
        const seq = typeof value.seq === 'number' ? value.seq : 0
        if (name === 'heartbeat' || seq === 0) {
          this.skipDelta = true
          this.lastHeartbeat = value.ts ?? new Date().toISOString()
          return { heartbeat: this.lastHeartbeat }
        }
        if (seq <= this.lastSeq) {
          this.skipDelta = true
          return {}
        }
        this.skipDelta = false
        this.lastSeq = seq
        saveLastSeq(this.runId, seq)
        const evt: AgentEvent = {
          seq,
          type: name,
          ts: value.ts ?? new Date().toISOString(),
          level: value.level ?? 'info',
          day: value.day ?? null,
          part: value.part ?? null,
          agent: value.agent ?? null,
          message: value.message ?? '',
          data: value.data ?? {},
          replay: value.replay ?? false,
        }
        return { events: [evt] }
      }
      default:
        return {}
    }
  }
}

const TERMINAL = new Set(['stopped', 'completed', 'budget_exhausted', 'failed'])

/** The server ends a run stream when the run is finished or paused; reconnecting then is pointless. */
export function streamShouldEnd(snapshot: RunSnapshot | null): boolean {
  const status = snapshot?.run.status
  return !!status && (TERMINAL.has(status) || status === 'paused')
}

export interface AttachHandlers {
  onState?: (snapshot: RunSnapshot) => void
  onEvents?: (events: AgentEvent[]) => void
  onHeartbeat?: (ts: string) => void
  onConnection?: (connected: boolean) => void
  onError?: (error: Error) => void
  /** The stream ended for good: the run is finished or paused, or the agent refused the command. */
  onEnded?: (snapshot: RunSnapshot | null) => void
}

export interface AttachOptions {
  /** `attach` by default; `resume` resumes a paused run and then streams like attach. */
  command?: 'attach' | 'resume'
  minBackoffMs?: number
  maxBackoffMs?: number
}

export interface AttachHandle {
  close: () => void
}

/**
 * Streams a run: snapshot, the events after `afterSeq`, then live events. When the connection drops, it reconnects
 * with exponential backoff and passes the last seen seq as afterSeq, so the agent replays only what was missed.
 * It stops when the run is finished or paused, when the agent refuses the command or the sign-in, or on `close()`.
 */
export function attach(runId: string, afterSeq: number, handlers: AttachHandlers, options: AttachOptions = {}): AttachHandle {
  const min = options.minBackoffMs ?? 1000
  const max = options.maxBackoffMs ?? 30000
  const state = new RunStreamState(runId, afterSeq)
  let cmd: 'attach' | 'resume' = options.command ?? 'attach'
  let backoff = min
  let closed = false
  let current: StreamHandle | null = null
  let timer: ReturnType<typeof setTimeout> | null = null

  const end = () => {
    if (closed) return
    closed = true
    current?.close()
    handlers.onConnection?.(false)
    handlers.onEnded?.(state.snapshot)
  }

  const schedule = (delay: number) => {
    if (closed) return
    current?.close()
    current = null
    handlers.onConnection?.(false)
    timer = setTimeout(connect, delay)
    backoff = Math.min(max, Math.max(min, backoff * 2))
  }

  function connect() {
    timer = null
    if (closed) return
    let connected = false
    const input = buildInput(cmd, {}, runId, state.lastSeq)
    cmd = 'attach' // a reconnect never re-sends a control command
    const handle = openStream(input, {
      next: (event) => {
        if (closed || current !== handle) return
        if (!connected) {
          connected = true
          backoff = min // the first event proves the connection works
          handlers.onConnection?.(true)
        }
        const u = state.handle(event)
        if (u.error) {
          handlers.onError?.(u.error)
          end()
          return
        }
        if (u.state) handlers.onState?.(u.state)
        if (u.events) handlers.onEvents?.(u.events)
        if (u.heartbeat) handlers.onHeartbeat?.(u.heartbeat)
        if (u.resync) schedule(0)
      },
      error: (e) => {
        if (closed || current !== handle) return
        handlers.onError?.(e instanceof Error ? e : new Error(String(e)))
        // Retrying cannot fix a rejected token; anything else (network, 5xx) is retried.
        if (e instanceof CommandError && e.code === 'unauthorized') end()
        else schedule(backoff)
      },
      complete: () => {
        if (closed || current !== handle) return
        // A stream that closes while the run is still active was cut by a proxy or the host: reconnect.
        if (streamShouldEnd(state.snapshot)) end()
        else schedule(connected ? min : backoff)
      },
    })
    current = handle
  }

  connect()
  return {
    close: () => {
      if (closed) return
      closed = true
      if (timer) clearTimeout(timer)
      current?.close()
    },
  }
}
