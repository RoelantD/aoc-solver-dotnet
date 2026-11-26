import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { AgentEvent, ApprovalRequest, DayInfo, PartInfo, PartState, RunSnapshot, RunSummary, UiState } from '../src/api/types'
import { ActivityFeed } from '../src/components/ActivityFeed'
import { AgentStrip } from '../src/components/AgentStrip'
import { ApprovalCard } from '../src/components/ApprovalCard'
import { ReplayBanner } from '../src/components/Banners'
import { Calendar } from '../src/components/Calendar'
import { startArgs } from '../src/components/StartRunDialog'
import { hasStar } from '../src/lib/format'
import { buildDays, countStars, latestHandoff, toUiState } from '../src/lib/runView'

const part = (uiState: UiState, extra: Partial<PartInfo> = {}): PartInfo => ({
  state: 'NOT_STARTED', uiState, escalated: false, statusReason: null, awaitingApproval: false, ...extra,
})

const ev = (seq: number, type: string, message: string, extra: Partial<AgentEvent> = {}): AgentEvent => ({
  seq, type, day: 2, part: 1, level: 'info', message, data: {}, agent: 'system', ts: new Date().toISOString(), ...extra,
})

const usage = (calls: number, costUsd: number) => ({ calls, inputTokens: 1000 * calls, outputTokens: 500 * calls, reasoningTokens: 0, latencyMs: 1000, costUsd })

describe('star rule', () => {
  it('only server-confirmed states earn a star', () => {
    const all: UiState[] = ['unsolved', 'solving', 'tests_passed', 'submitted', 'verified', 'previously_completed', 'failed', 'blocked', 'skipped']
    expect(all.filter(hasStar)).toEqual(['verified', 'previously_completed'])
  })

  it('maps part states; only VERIFIED becomes a star state', () => {
    const states: PartState[] = ['NOT_STARTED', 'ANALYZING', 'ESCALATING', 'WAITING_FOR_APPROVAL', 'SUBMITTING', 'WAITING_FOR_COOLDOWN', 'VERIFIED', 'FAILED', 'BLOCKED', 'SKIPPED']
    expect(states.map((s) => toUiState(s))).toEqual(['unsolved', 'solving', 'solving', 'tests_passed', 'submitted', 'submitted', 'verified', 'failed', 'blocked', 'skipped'])
    expect(states.filter((s) => hasStar(toUiState(s)))).toEqual(['VERIFIED'])
    expect(toUiState('VERIFIED', true)).toBe('previously_completed')
  })
})

describe('snapshot view model', () => {
  it('builds the calendar from snapshot.calendar plus the timeline', () => {
    const calendar = Array.from({ length: 25 }, () => ['NOT_STARTED', 'NOT_STARTED'] as PartState[])
    calendar[0] = ['VERIFIED', 'VERIFIED']
    calendar[1] = ['WAITING_FOR_APPROVAL', 'NOT_STARTED']
    calendar[2] = ['FAILED', 'BLOCKED']
    const snapshot = {
      run: {} as RunSummary,
      calendar,
      titles: ['Alpha', 'Beta', ...Array(23).fill(null)],
      stars: 2,
      pendingApprovals: [{ requestId: 'a1', day: 2, part: 1, answer: '42', state: 'pending' }],
      usage: { byAgent: {}, byModel: {}, totals: usage(0, 0) },
    } as RunSnapshot
    const events = [
      ev(1, 'aoc.previously_completed', 'done before', { day: 1, part: 2 }),
      ev(2, 'agent.handoff', 'Coder → Escalation Coder', { day: 3, part: 1, data: { from: 'coder', to: 'escalation-coder', reason: '3 failures' } }),
      ev(3, 'part.failed', 'Day 03 Part 1 failed: attempts exhausted', { day: 3, part: 1, level: 'error' }),
    ]
    const days = buildDays(snapshot, events)
    expect(days).toHaveLength(25)
    expect(days[0].title).toBe('Alpha')
    expect(days[0].parts['1'].uiState).toBe('verified')
    expect(days[0].parts['2'].uiState).toBe('previously_completed')
    expect(days[1].parts['1']).toMatchObject({ uiState: 'tests_passed', awaitingApproval: true })
    expect(days[2].parts['1']).toMatchObject({ uiState: 'failed', escalated: true, statusReason: 'Day 03 Part 1 failed: attempts exhausted' })
    expect(countStars(days)).toEqual({ total: 2, earnedThisRun: 1, previouslyCompleted: 1 })
  })
})

describe('Calendar', () => {
  it('renders 25 days with independent part states', () => {
    const days: DayInfo[] = [
      { day: 1, title: 'Alpha', parts: { '1': part('verified'), '2': part('submitted') } },
      { day: 2, title: 'Beta', parts: { '1': part('tests_passed'), '2': part('blocked', { statusReason: 'Part 1 failed' }) } },
      { day: 3, title: null, parts: { '1': part('failed'), '2': part('unsolved') } },
    ]
    render(<Calendar days={days} dayFrom={1} dayTo={25} lastStar={null} currentDay={2} />)
    expect(screen.getAllByRole('listitem')).toHaveLength(25)
    expect(screen.getByLabelText('Day 1 part 1: Verified by Advent of Code')).toBeInTheDocument()
    expect(screen.getByLabelText('Day 1 part 2: Submitted')).toBeInTheDocument()
    expect(screen.getByLabelText('Day 2 part 1: Tests passed')).toBeInTheDocument()
    expect(screen.getByLabelText('Day 2 part 2: Blocked — Part 1 failed')).toBeInTheDocument()
    expect(screen.getByLabelText('Day 3 part 1: Failed')).toBeInTheDocument()
  })
})

describe('ActivityFeed', () => {
  it('shows real event messages grouped by day/part and hides verbose types', () => {
    const events = [
      ev(1, 'agent.step', 'Loading puzzle…'),
      ev(2, 'part.state', 'NOT_STARTED → FETCHING'),
      ev(3, 'test.result', 'Test failed: expected 4, received 3', { level: 'error' }),
      ev(4, 'agent.escalation', 'Escalating GPT → Opus: duplicate'),
      ev(5, 'agent.active', 'System step', { agent: 'system' }),
      ev(6, 'submit.result', 'Advent of Code accepted the answer', { level: 'success' }),
    ]
    render(<ActivityFeed events={events} />)
    expect(screen.getByText('Day 02 - Part 1')).toBeInTheDocument()
    expect(screen.getByText('Test failed: expected 4, received 3')).toBeInTheDocument()
    expect(screen.getByText('Advent of Code accepted the answer')).toBeInTheDocument()
    expect(screen.queryByText('NOT_STARTED → FETCHING')).not.toBeInTheDocument()
    expect(screen.queryByText(/duplicate/)).not.toBeInTheDocument()
    expect(screen.queryByText('System step')).not.toBeInTheDocument()
  })
})

describe('AgentStrip', () => {
  const events = [
    ev(1, 'agent.active', 'Analyst (GPT-6.1 Sol) is working', { agent: 'analyst', data: { agent: 'analyst', model: 'GPT-6.1 Sol' } }),
    ev(2, 'agent.active', 'Coder (GPT-6.1 Sol) is working', { agent: 'coder', data: { agent: 'coder', model: 'GPT-6.1 Sol' } }),
    ev(3, 'agent.handoff', 'Coder → Escalation Coder', {
      agent: 'coder', level: 'warning',
      data: { from: 'coder', to: 'escalation-coder', reason: '3 unsuccessful attempts', handoverSummary: '…', attempt: 4 },
    }),
    ev(4, 'agent.active', 'Escalation Coder (Claude Opus 5.5) is working', { agent: 'escalation-coder', data: { agent: 'escalation-coder', model: 'Claude Opus 5.5' } }),
  ]
  const runUsage = {
    byAgent: { analyst: usage(1, 0.01), coder: usage(3, 0.12), 'escalation-coder': usage(1, 0.5) },
    byModel: {},
    totals: usage(5, 0.63),
  }

  it('shows the active agent with its model, the handoff with its reason, and usage per agent', () => {
    render(<AgentStrip activeAgent="escalation-coder" usage={runUsage} events={events} running />)
    expect(screen.getByTestId('active-agent')).toHaveTextContent('Escalation Coder')
    expect(screen.getByTestId('active-agent')).toHaveTextContent('Claude Opus 5.5')
    const handoff = screen.getByRole('status', { name: 'Agent handoff' })
    expect(handoff).toHaveTextContent('Coder')
    expect(handoff).toHaveTextContent('Escalation Coder')
    expect(handoff).toHaveTextContent('3 unsuccessful attempts')
    expect(screen.getByTestId('agent-coder')).toHaveTextContent('3 calls')
    expect(screen.getByTestId('agent-coder')).toHaveTextContent('$0.12')
    expect(screen.getByTestId('agent-escalation-coder')).toHaveTextContent('$0.50')
    expect(screen.getByTestId('agent-analyst')).toHaveTextContent('GPT-6.1 Sol')
  })

  it('is idle without an active agent and shows no handoff for another part', () => {
    render(<AgentStrip activeAgent={null} usage={null} events={events} day={5} part={1} />)
    expect(screen.getByTestId('active-agent')).toHaveTextContent('Idle')
    expect(screen.queryByRole('status', { name: 'Agent handoff' })).not.toBeInTheDocument()
    expect(screen.getByTestId('agent-coder')).toHaveTextContent('no calls yet')
    expect(latestHandoff(events, 2, 1)?.seq).toBe(3)
    expect(latestHandoff(events, 5, 1)).toBeNull()
  })
})

describe('ApprovalCard', () => {
  const approvals: ApprovalRequest[] = [
    { requestId: 'req-1', day: 1, part: 1, answer: '1234', state: 'pending' },
    { requestId: 'req-0', day: 1, part: 1, answer: '99', state: 'rejected' },
  ]

  it('lists pending answers only and sends approve / reject with the request id', async () => {
    const onDecide = vi.fn().mockResolvedValue(undefined)
    render(<ApprovalCard approvals={approvals} onDecide={onDecide} />)
    expect(screen.getByLabelText('Candidate answer')).toHaveTextContent('1234')
    expect(screen.queryByText('99')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Approve/ }))
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith('approve', 'req-1'))
    fireEvent.click(screen.getByRole('button', { name: /Reject/ }))
    await waitFor(() => expect(onDecide).toHaveBeenCalledWith('reject', 'req-1'))
  })

  it('shows the command error and renders nothing without pending approvals', async () => {
    const onDecide = vi.fn().mockRejectedValue(new Error('no pending approval req-1'))
    const { rerender } = render(<ApprovalCard approvals={approvals} onDecide={onDecide} />)
    fireEvent.click(screen.getByRole('button', { name: /Approve/ }))
    expect(await screen.findByText('no pending approval req-1')).toBeInTheDocument()
    rerender(<ApprovalCard approvals={[approvals[1]]} onDecide={onDecide} />)
    expect(screen.queryByLabelText('Pending approvals')).not.toBeInTheDocument()
  })
})

describe('StartRunDialog arguments', () => {
  it('maps the dialog to the start command args', () => {
    const base = { year: 2024, from: 1, to: 3, attempts: 6, escalateAfter: 3, spend: 20, timeout: 30 }
    expect(startArgs({ ...base, authorized: true, manual: false })).toEqual({
      year: 2024, dayFrom: 1, dayTo: 3, authorizeSubmissions: true, approvalMode: 'auto',
      limits: { maxAttemptsPerPart: 6, escalateAfterFailures: 3, maxSpendUsd: 20, execTimeoutS: 30 },
    })
    expect(startArgs({ ...base, authorized: false, manual: false }).approvalMode).toBe('manual')
  })
})

describe('Replay banner', () => {
  it('labels replay runs and never live runs', () => {
    const base = { id: 'run-2', mode: 'replay' } as RunSummary
    const { rerender } = render(<ReplayBanner run={base} />)
    expect(screen.getByRole('alert')).toHaveTextContent('REPLAY of a recorded run')
    rerender(<ReplayBanner run={{ ...base, mode: 'live' }} />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
