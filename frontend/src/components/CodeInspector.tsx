import { CheckCircle2, Clock, XCircle } from 'lucide-react'
import { Highlight, themes } from 'prism-react-renderer'
import { useEffect, useState } from 'react'
import { command } from '../api/agent'
import type { AgentEvent, DetailSnapshot } from '../api/types'
import { agentLabel, agentTone } from '../lib/runView'
import { cx } from './ui'

export function CodeBlock({ code, maxHeight, fontSize = 13 }: { code: string; maxHeight?: string; fontSize?: number }) {
  return (
    <Highlight theme={themes.nightOwl} code={code.replace(/\n$/, '')} language="python">
      {({ className, style, tokens, getLineProps, getTokenProps }) => (
        <pre
          className={cx(className, 'scroll-thin overflow-auto rounded-lg p-3 font-mono')}
          style={{ ...style, background: '#070b16', maxHeight, fontSize }}
        >
          {tokens.map((line, i) => (
            <div key={i} {...getLineProps({ line })}>
              <span className="mr-4 inline-block w-7 select-none text-right text-ink-600">{i + 1}</span>
              {line.map((token, k) => (
                <span key={k} {...getTokenProps({ token })} />
              ))}
            </div>
          ))}
        </pre>
      )}
    </Highlight>
  )
}

interface Failure {
  index: number
  expected?: string | null
  actual?: string | null
  error?: string | null
}

/** Test and execution results of one revision, from the timeline's `test.result` and `exec.input` events. */
export function revisionResults(events: AgentEvent[], day: number, part: number, revision: number): AgentEvent[] {
  return events.filter(
    (e) => (e.type === 'test.result' || e.type === 'exec.input') && e.day === day && e.part === part && e.data?.revision === revision,
  )
}

function ResultSummary({ e }: { e: AgentEvent }) {
  const isTest = e.type === 'test.result'
  const ok = isTest ? e.data?.passed === true : e.data?.status === 'ok'
  const timedOut = e.data?.status === 'timeout' || /timed out/i.test(e.message)
  const failures = (isTest ? (e.data?.failures as Failure[] | undefined) : undefined) ?? []
  return (
    <div className="rounded-lg border border-ink-700 bg-ink-900/70 p-3 text-sm">
      <div className="flex items-center gap-2">
        {timedOut ? <Clock size={15} className="text-warn" /> : ok ? <CheckCircle2 size={15} className="text-ok" /> : <XCircle size={15} className="text-bad" />}
        <span className="font-medium">{isTest ? 'Example tests' : 'Real input'}</span>
        <span className="text-ink-400">
          {isTest ? `${e.data?.total ?? 0} case(s)` : (e.data?.status ?? '')}
          {typeof e.data?.duration_ms === 'number' ? ` · ${(e.data.duration_ms / 1000).toFixed(2)}s` : ''}
        </span>
      </div>
      <p className={cx('mt-1', ok ? 'text-ink-300' : 'text-bad')}>{e.message}</p>
      {failures.length > 0 && (
        <table className="mt-2 w-full text-left text-xs">
          <thead className="text-ink-400">
            <tr>
              <th className="py-1 font-medium">#</th>
              <th className="font-medium">expected</th>
              <th className="font-medium">actual</th>
              <th className="font-medium">error</th>
            </tr>
          </thead>
          <tbody className="font-mono">
            {failures.map((c) => (
              <tr key={c.index} className="border-t border-ink-800 align-top">
                <td className="py-1">{c.index + 1}</td>
                <td>{c.expected ?? <span className="text-ink-400">n/a</span>}</td>
                <td className="max-w-[16rem] truncate">{c.actual ?? '—'}</td>
                <td className="text-bad">{c.error ? c.error.split('\n')[0] : 'fail'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {!isTest && e.data?.answer && (
        <div className="mt-2 font-mono text-sm">
          candidate answer: <span className="text-star">{e.data.answer}</span>
        </div>
      )}
    </div>
  )
}

/** Part detail (`detail` command), refetched when `version` changes (new code or results in the timeline). */
export function usePartDetail(runId: string | null, day: number | null, part: number | null, version: number) {
  const [detail, setDetail] = useState<DetailSnapshot | null>(null)
  useEffect(() => {
    if (runId == null || day == null || part == null) {
      setDetail(null)
      return
    }
    let alive = true
    command<DetailSnapshot>('detail', { day, part }, runId)
      .then((d) => alive && setDetail(d))
      .catch(() => {})
    return () => {
      alive = false
    }
  }, [runId, day, part, version])
  return detail
}

export function CodeInspector({
  runId,
  day,
  part,
  version,
  events,
}: {
  runId: string | null
  day: number | null
  part: number | null
  version: number
  events: AgentEvent[]
}) {
  const detail = usePartDetail(runId, day, part, version)
  const revs = detail?.revisions ?? []
  const [sel, setSel] = useState<number | null>(null)
  useEffect(() => setSel(null), [day, part])
  if (day == null || part == null) return <p className="p-4 text-sm text-ink-400">Select a part in the calendar.</p>
  if (!revs.length) return <p className="p-4 text-sm text-ink-400">No code generated yet for Day {day} Part {part}.</p>
  const current = revs.find((r) => r.revision === sel) ?? revs[revs.length - 1]
  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3 p-3">
      <div className="flex flex-wrap items-center gap-1.5" role="tablist" aria-label="Code revisions">
        <span className="mr-1 text-xs text-ink-400">Day {day} · Part {part} · revisions</span>
        {revs.map((r) => (
          <button
            key={r.revision}
            role="tab"
            aria-selected={r.revision === current.revision}
            onClick={() => setSel(r.revision)}
            className={cx(
              'rounded-md border px-2 py-0.5 font-mono text-xs',
              r.revision === current.revision ? 'border-sol bg-sol/15 text-sol' : 'border-ink-600 text-ink-300 hover:bg-ink-700',
              r.agent === 'escalation-coder' && 'border-opus/60 text-opus',
            )}
            title={agentLabel(r.agent)}
          >
            r{r.revision}
          </button>
        ))}
        <span className={cx('ml-auto text-xs', agentTone(current.agent))}>{agentLabel(current.agent)}</span>
      </div>
      <CodeBlock code={current.code} maxHeight="26rem" />
      <div className="flex flex-col gap-2">
        {revisionResults(events, day, part, current.revision).map((e) => (
          <ResultSummary key={e.seq} e={e} />
        ))}
      </div>
    </div>
  )
}
