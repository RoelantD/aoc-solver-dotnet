import { EventType } from '@ag-ui/client'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import {
  CommandError,
  RunStreamState,
  attach,
  buildInput,
  command,
  loadLastSeq,
  requestUrl,
  setTransport,
  streamShouldEnd,
} from '../src/api/agent'
import type { AgentEvent, RunSnapshot } from '../src/api/types'

const snapshot = (status = 'running', calendar = [['NOT_STARTED', 'NOT_STARTED']]): RunSnapshot =>
  ({
    run: { id: 'run-1', year: 2024, dayFrom: 1, dayTo: 1, mode: 'live', status, approvalMode: 'manual', autoSubmit: false, lastSeq: 0, createdAt: '2026-11-24T10:00:00Z', costUsd: 0, leaseHeld: true },
    calendar,
    titles: [null],
    stars: 0,
    pendingApprovals: [],
    usage: { byAgent: {}, byModel: {}, totals: { calls: 0, inputTokens: 0, outputTokens: 0, reasoningTokens: 0, latencyMs: 0, costUsd: 0 } },
  }) as RunSnapshot

const custom = (name: string, seq: number, extra: Record<string, unknown> = {}) => ({
  type: EventType.CUSTOM,
  name,
  value: { seq, ts: '2026-11-24T10:00:00Z', level: 'info', message: `${name} #${seq}`, ...extra },
})
const delta = (path: string, value: unknown) => ({ type: EventType.STATE_DELTA, delta: [{ op: 'replace', path, value }] })
const snap = (s: RunSnapshot) => ({ type: EventType.STATE_SNAPSHOT, snapshot: s })

function sse(events: object[], headers: Record<string, string> = {}): Response {
  const body = [{ type: EventType.RUN_STARTED, threadId: 't', runId: 'r' }, ...events, { type: EventType.RUN_FINISHED, threadId: 't', runId: 'r' }]
    .map((e) => `data: ${JSON.stringify(e)}\n\n`)
    .join('')
  return new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream', ...headers } })
}

interface Captured {
  url: string
  body: { threadId: string; runId: string; messages: unknown[]; forwardedProps: { command: string; afterSeq: number; args: Record<string, unknown> } }
  headers: Record<string, string>
}

function fakeAgent(responses: Response[], url = 'http://localhost:8088/invocations', token: string | null = null) {
  const calls: Captured[] = []
  const restore = setTransport({
    url,
    getToken: async () => token,
    fetch: async (u, init) => {
      calls.push({ url: u, body: JSON.parse(String(init.body)), headers: init.headers as Record<string, string> })
      const next = responses.shift()
      if (!next) throw new Error('no more responses')
      return next
    },
  })
  return { calls, restore }
}

beforeEach(() => sessionStorage.clear())
let restore: (() => void) | null = null
afterEach(() => {
  restore?.()
  restore = null
})

describe('RunStreamState (snapshot, deltas, seq dedup)', () => {
  it('replaces state on STATE_SNAPSHOT and applies STATE_DELTA', () => {
    const s = new RunStreamState('run-1')
    s.handle(snap(snapshot()) as never)
    s.handle(custom('part.state', 1) as never)
    const u = s.handle(delta('/calendar/0/0', 'ANALYZING') as never)
    expect(u.state?.calendar[0][0]).toBe('ANALYZING')
    s.handle(custom('agent.active', 2, { data: { agent: 'coder', model: 'GPT-6.1 Sol' } }) as never)
    expect(s.handle(delta('/activeAgent', 'coder') as never).state?.activeAgent).toBe('coder')
  })

  it('deduplicates CUSTOM events by seq and skips the delta of a duplicate', () => {
    const s = new RunStreamState('run-1', 0)
    s.handle(snap(snapshot()) as never)
    expect(s.handle(custom('part.state', 1) as never).events?.map((e) => e.seq)).toEqual([1])
    s.handle(delta('/calendar/0/0', 'VERIFIED') as never)
    // a fresh snapshot after a reconnect, then a replay of seq 1 with a stale delta
    s.handle(snap(snapshot('running', [['VERIFIED', 'TESTING']])) as never)
    expect(s.handle(custom('part.state', 1) as never).events).toBeUndefined()
    expect(s.handle(delta('/calendar/0/0', 'ANALYZING') as never).state).toBeUndefined()
    expect(s.snapshot?.calendar[0][0]).toBe('VERIFIED')
    expect(s.handle(custom('test.result', 2) as never).events?.[0].type).toBe('test.result')
    expect(s.lastSeq).toBe(2)
    expect(loadLastSeq('run-1')).toBe(2)
  })

  it('keeps heartbeats (seq 0) out of the timeline but reports them', () => {
    const s = new RunStreamState('run-1')
    const u = s.handle({ type: EventType.CUSTOM, name: 'heartbeat', value: { seq: 0, ts: '2026-11-24T10:00:15Z' } } as never)
    expect(u.events).toBeUndefined()
    expect(u.heartbeat).toBe('2026-11-24T10:00:15Z')
    expect(s.lastHeartbeat).toBe('2026-11-24T10:00:15Z')
    expect(s.lastSeq).toBe(0)
  })

  it('turns command.error and RUN_ERROR into CommandError', () => {
    const s = new RunStreamState('run-1')
    const e1 = s.handle({ type: EventType.CUSTOM, name: 'command.error', value: { seq: 0, message: 'run x not found', data: { code: 'not_found' } } } as never)
    expect(e1.error).toBeInstanceOf(CommandError)
    expect(e1.error?.code).toBe('not_found')
    const e2 = s.handle({ type: EventType.RUN_ERROR, message: 'boom', code: 'internal' } as never)
    expect(e2.error?.message).toBe('boom')
  })

  it('knows when the server ends the stream for good', () => {
    expect(streamShouldEnd(snapshot('running'))).toBe(false)
    expect(streamShouldEnd(snapshot('paused'))).toBe(true)
    expect(streamShouldEnd(snapshot('completed'))).toBe(true)
    expect(streamShouldEnd(null)).toBe(false)
  })
})

describe('request envelope', () => {
  it('builds an AG-UI RunAgentInput with the command in forwardedProps', () => {
    const input = buildInput('retry', { day: 1, part: 2 }, 'run-1', 7)
    expect(input).toMatchObject({ threadId: 'run-1', messages: [], state: {}, tools: [], context: [], forwardedProps: { command: 'retry', args: { day: 1, part: 2 }, afterSeq: 7 } })
    expect(input.runId).toMatch(/[0-9a-f-]{36}/)
    expect(buildInput('list').runId).not.toBe(input.runId)
  })

  it('appends agent_session_id only for a hosted URL', () => {
    sessionStorage.setItem('aps.agentSessionId', 'sess-1')
    expect(requestUrl('http://localhost:8088/invocations')).toBe('http://localhost:8088/invocations')
    const hosted = requestUrl('https://acct.services.ai.azure.com/api/projects/p/agents/aps-solver/endpoint/protocols/invocations?api-version=v1')
    expect(hosted).toContain('api-version=v1')
    expect(hosted).toContain('agent_session_id=sess-1')
  })
})

describe('command()', () => {
  it('resolves with the snapshot and persists the session header', async () => {
    const hostedUrl = 'https://acct.services.ai.azure.com/api/projects/p/agents/aps-solver/endpoint/protocols/invocations?api-version=v1'
    const f = fakeAgent([sse([snap(snapshot())], { 'x-agent-session-id': 'sess-9' }), sse([{ type: EventType.STATE_SNAPSHOT, snapshot: { runs: [] } }])], hostedUrl, 'tok')
    restore = f.restore
    const s = await command<RunSnapshot>('pause', {}, 'run-1')
    expect(s.run.id).toBe('run-1')
    expect(f.calls[0].body.forwardedProps.command).toBe('pause')
    expect(f.calls[0].body.threadId).toBe('run-1')
    expect(f.calls[0].headers.Authorization).toBe('Bearer tok')
    await command('list')
    expect(f.calls[1].url).toContain('agent_session_id=sess-9')
  })

  it('rejects with the command.error message and code', async () => {
    const f = fakeAgent([sse([{ type: EventType.CUSTOM, name: 'command.error', value: { seq: 0, message: 'run is completed, not paused', data: { code: 'invalid_command', command: 'resume' } } }])])
    restore = f.restore
    const err = await command('resume', {}, 'run-1').catch((e) => e)
    expect(err).toBeInstanceOf(CommandError)
    expect(err.message).toBe('run is completed, not paused')
    expect(err.code).toBe('invalid_command')
    expect(f.calls[0].headers.Authorization).toBeUndefined()
  })
})

describe('attach()', () => {
  it('reconnects with afterSeq = last seen seq and deduplicates the timeline', async () => {
    const f = fakeAgent([
      // first connection drops while the run is still running
      sse([snap(snapshot()), custom('run.started', 1), delta('/run/status', 'running'), custom('agent.step', 2)]),
      // reconnect: fresh snapshot, a duplicate of seq 2, then new events until the run completes
      sse([
        snap(snapshot('running', [['ANALYZING', 'NOT_STARTED']])),
        custom('agent.step', 2),
        { type: EventType.CUSTOM, name: 'heartbeat', value: { seq: 0, ts: '2026-11-24T10:00:30Z' } },
        custom('star.earned', 3),
        delta('/run/status', 'completed'),
      ]),
    ])
    restore = f.restore
    const timeline: AgentEvent[] = []
    let heartbeat: string | null = null
    const ended = await new Promise<RunSnapshot | null>((resolve) => {
      attach('run-1', 0, {
        onEvents: (e) => timeline.push(...e),
        onHeartbeat: (ts) => (heartbeat = ts),
        onEnded: resolve,
      }, { minBackoffMs: 1, maxBackoffMs: 5 })
    })
    expect(f.calls.map((c) => [c.body.forwardedProps.command, c.body.forwardedProps.afterSeq])).toEqual([
      ['attach', 0],
      ['attach', 2],
    ])
    expect(f.calls.every((c) => c.body.threadId === 'run-1')).toBe(true)
    expect(timeline.map((e) => e.seq)).toEqual([1, 2, 3])
    expect(heartbeat).toBe('2026-11-24T10:00:30Z')
    expect(ended?.run.status).toBe('completed')
    expect(loadLastSeq('run-1')).toBe(3)
  })

  it('retries with backoff after a network error and stops on command.error', async () => {
    const f = fakeAgent([
      new Response('busy', { status: 503 }),
      sse([{ type: EventType.CUSTOM, name: 'command.error', value: { seq: 0, message: "run 'run-1' not found", data: { code: 'not_found' } } }]),
    ])
    restore = f.restore
    const errors: string[] = []
    await new Promise<void>((resolve) => {
      attach('run-1', 5, { onError: (e) => errors.push(e.message), onEnded: () => resolve() }, { minBackoffMs: 1, maxBackoffMs: 5 })
    })
    expect(f.calls).toHaveLength(2)
    expect(f.calls[1].body.forwardedProps.afterSeq).toBe(5)
    expect(errors[0]).toMatch(/503/)
    expect(errors.at(-1)).toBe("run 'run-1' not found")
  })
})
