// Shapes of the AG-UI shared state and CUSTOM events sent by the .NET agent (src/Aps.Agent/Protocol/Snapshots.cs).
// Properties serialized as null are omitted by the server, so nullable fields are optional here.

export type RunStatus = 'created' | 'running' | 'paused' | 'stopped' | 'completed' | 'budget_exhausted' | 'failed'

export type PartState =
  | 'NOT_STARTED'
  | 'FETCHING'
  | 'ANALYZING'
  | 'GENERATING'
  | 'TESTING'
  | 'REPAIRING'
  | 'ESCALATING'
  | 'EXECUTING_INPUT'
  | 'READY_TO_SUBMIT'
  | 'WAITING_FOR_APPROVAL'
  | 'SUBMITTING'
  | 'WAITING_FOR_COOLDOWN'
  | 'VERIFIED'
  | 'BLOCKED'
  | 'FAILED'
  | 'SKIPPED'

/** Agent ids. "system" marks deterministic steps (fetching, testing, submitting). */
export type AgentId = 'analyst' | 'coder' | 'escalation-coder' | 'system'

export type UiState =
  | 'unsolved'
  | 'solving'
  | 'tests_passed'
  | 'submitted'
  | 'verified'
  | 'previously_completed'
  | 'failed'
  | 'blocked'
  | 'skipped'

/** Run summary (Snapshots.RunSummary); also the rows of the `list` snapshot. */
export interface RunSummary {
  id: string
  year: number
  dayFrom: number
  dayTo: number
  mode: 'live' | 'replay'
  status: RunStatus
  stopReason?: string | null
  approvalMode: 'auto' | 'manual'
  autoSubmit: boolean
  currentDay?: number | null
  currentPart?: number | null
  lastSeq: number
  createdAt: string
  startedAt?: string | null
  finishedAt?: string | null
  costUsd: number
  leaseHeld: boolean
}

export interface UsageTotals {
  calls: number
  inputTokens: number
  outputTokens: number
  reasoningTokens: number
  latencyMs: number
  costUsd: number
}

export interface RunUsage {
  byAgent: Record<string, UsageTotals>
  byModel: Record<string, UsageTotals>
  totals: UsageTotals
}

export interface ApprovalRequest {
  requestId: string
  day: number
  part: number
  answer: string
  state: 'pending' | 'approved' | 'rejected'
  decidedAt?: string | null
  decidedBy?: string | null
  appliedAt?: string | null
}

/** STATE_SNAPSHOT for one run (attach, start, resume and the control commands). */
export interface RunSnapshot {
  run: RunSummary
  calendar: PartState[][]
  titles: (string | null)[]
  stars: number
  activeAgent?: AgentId | string | null
  pendingApprovals: ApprovalRequest[]
  usage: RunUsage
}

/** STATE_SNAPSHOT of the `list` command. */
export interface ListSnapshot {
  runs: RunSummary[]
}

export interface Handoff {
  from: string
  to: string
  reason: string
  handoverSummary: string
  at: string
}

export interface PartProgress {
  id: string
  runId: string
  day: number
  part: number
  title?: string | null
  state: PartState
  previouslyCompleted: boolean
  testsPassed: boolean
  candidateAnswer?: string | null
  acceptedAnswer?: string | null
  attemptsUsed: number
  submissionsUsed: number
  currentModel?: string | null
  activeAgent?: string | null
  escalated: boolean
  statusReason?: string | null
  handoffs: Handoff[]
  updatedAt: string
}

export interface Attempt {
  id: string
  day: number
  part: number
  number: number
  attemptKind: string
  agent: string
  modelKey: string
  summary?: string | null
  algorithm?: string | null
  diagnosis?: string | null
  outcome: string
  startedAt: string
  finishedAt?: string | null
}

export interface CodeRevision {
  id: string
  day: number
  part: number
  revision: number
  agent: string
  modelKey: string
  code: string
  sha256: string
  createdAt: string
}

export interface Submission {
  id: string
  year: number
  day: number
  part: number
  answer: string
  outcome: string
  message?: string | null
  waitSeconds?: number | null
  submittedAt: string
  respondedAt?: string | null
}

/** STATE_SNAPSHOT of the `detail` command. */
export interface DetailSnapshot {
  part?: PartProgress | null
  attempts: Attempt[]
  revisions: CodeRevision[]
  submissions: Submission[]
  usage: RunUsage
}

/** A domain event from an AG-UI CUSTOM event: `type` is the event name, the rest is its value. */
export interface AgentEvent {
  seq: number
  type: string
  ts: string
  level: 'info' | 'success' | 'warning' | 'error'
  day?: number | null
  part?: number | null
  agent?: string | null
  message: string
  // Event-specific payload; components read the fields they know per event type.
  data?: Record<string, any> | null
  replay?: boolean
}

/** Per-part view model for the calendar, derived from the snapshot and the timeline. */
export interface PartInfo {
  state: PartState
  uiState: UiState
  escalated: boolean
  statusReason: string | null
  awaitingApproval: boolean
}

export interface DayInfo {
  day: number
  title: string | null
  parts: Record<string, PartInfo>
}

/** Arguments of the `start` command (RunStreamAgent.CreateRunAsync). */
export interface StartArgs {
  year: number
  dayFrom: number
  dayTo: number
  authorizeSubmissions: boolean
  approvalMode: 'auto' | 'manual'
  limits?: {
    maxAttemptsPerPart?: number
    escalateAfterFailures?: number
    maxSubmissionsPerPart?: number
    execTimeoutS?: number
    maxSpendUsd?: number
  }
}
