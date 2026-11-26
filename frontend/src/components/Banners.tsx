import { History } from 'lucide-react'
import type { RunSummary } from '../api/types'

export function ReplayBanner({ run }: { run: RunSummary | null | undefined }) {
  if (!run || run.mode !== 'replay') return null
  return (
    <div
      role="alert"
      className="sticky top-0 z-30 flex items-center justify-center gap-2 border-b border-warn/50 bg-warn/20 px-4 py-2 text-sm font-semibold tracking-wide text-warn"
    >
      <History size={18} />
      REPLAY of a recorded run — not live. No Advent of Code, model or sandbox calls are made.
    </div>
  )
}
