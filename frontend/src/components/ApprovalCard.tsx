import { ShieldQuestion, ThumbsDown, ThumbsUp } from 'lucide-react'
import { useState } from 'react'
import type { ApprovalRequest } from '../api/types'
import { Button, cx } from './ui'

/**
 * Manual-approval card: every pending candidate answer with Approve / Reject. The decision is stored by the
 * `approve` / `reject` command and applied exactly once by the run's leaseholder.
 */
export function ApprovalCard({
  approvals,
  onDecide,
  large = false,
}: {
  approvals: ApprovalRequest[] | null | undefined
  onDecide: (decision: 'approve' | 'reject', requestId: string) => Promise<void>
  large?: boolean
}) {
  const [busy, setBusy] = useState<string | null>(null)
  const [err, setErr] = useState<string | null>(null)
  const pending = (approvals ?? []).filter((a) => a.state === 'pending')
  if (!pending.length) return null
  const decide = async (decision: 'approve' | 'reject', requestId: string) => {
    setBusy(requestId)
    setErr(null)
    try {
      await onDecide(decision, requestId)
    } catch (e) {
      setErr((e as Error).message)
    } finally {
      setBusy(null)
    }
  }
  return (
    <section
      aria-label="Pending approvals"
      className={cx('animate-slide-in rounded-2xl border border-warn/60 bg-warn/10', large ? 'p-5' : 'p-3')}
    >
      <div className={cx('mb-2 flex items-center gap-2 font-semibold text-warn', large ? 'text-xl' : 'text-sm')}>
        <ShieldQuestion size={large ? 24 : 16} /> Waiting for your approval
      </div>
      <ul className="flex flex-col gap-2">
        {pending.map((a) => (
          <li key={a.requestId} className="flex flex-wrap items-center gap-3 rounded-lg border border-ink-700 bg-ink-900/60 px-3 py-2">
            <span className={cx('font-mono text-ink-300', large ? 'text-lg' : 'text-sm')}>
              Day {String(a.day).padStart(2, '0')} · Part {a.part}
            </span>
            <span className={cx('font-mono font-semibold text-star', large ? 'text-2xl' : 'text-base')} aria-label="Candidate answer">
              {a.answer}
            </span>
            <span className="ml-auto flex gap-2">
              <Button variant="primary" disabled={busy !== null} onClick={() => decide('approve', a.requestId)}>
                <ThumbsUp size={15} /> Approve
              </Button>
              <Button variant="danger" disabled={busy !== null} onClick={() => decide('reject', a.requestId)}>
                <ThumbsDown size={15} /> Reject
              </Button>
            </span>
          </li>
        ))}
      </ul>
      {err && <p className="mt-2 text-sm text-bad">{err}</p>}
    </section>
  )
}
