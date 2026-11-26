import { EventType } from '@ag-ui/client'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { setTransport } from '../src/api/agent'
import { PreflightPanel } from '../src/components/PreflightPanel'

const check = (name: string, ok: boolean, extra: Record<string, unknown> = {}) => ({
  type: EventType.CUSTOM,
  name: 'preflight.check',
  value: { seq: 0, ts: '2026-11-25T10:00:00Z', level: ok ? 'success' : 'error', message: name, data: { check: name, ok, detail: `${name} detail`, ms: 1200, ...extra } },
})

function sse(events: object[]): Response {
  const body = [{ type: EventType.RUN_STARTED, threadId: 't', runId: 'r' }, ...events, { type: EventType.RUN_FINISHED, threadId: 't', runId: 'r' }]
    .map((e) => `data: ${JSON.stringify(e)}\n\n`)
    .join('')
  return new Response(body, { status: 200, headers: { 'content-type': 'text/event-stream' } })
}

let restore: (() => void) | null = null
afterEach(() => {
  restore?.()
  restore = null
})

describe('PreflightPanel', () => {
  it('sends preflight for the chosen range and shows each check with the missing permission', async () => {
    const sent: { command: string; args: Record<string, unknown> }[] = []
    restore = setTransport({
      url: 'http://localhost:8088/invocations',
      getToken: async () => null,
      fetch: async (_u, init) => {
        sent.push(JSON.parse(String(init.body)).forwardedProps)
        return sse([
          check('identity', true),
          check('store', false, { missingPermission: 'Microsoft.DocumentDB/databaseAccounts/readMetadata' }),
          {
            type: EventType.STATE_SNAPSHOT,
            snapshot: { preflight: { ok: false, year: 2023, dayFrom: 2, dayTo: 4, passed: 1, failed: ['store'], skipped: [], checks: [] } },
          },
        ])
      },
    })

    render(<PreflightPanel year={2023} dayFrom={2} dayTo={4} />)
    fireEvent.click(screen.getByRole('button', { name: /run preflight/i }))

    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Failed: Run store.'))
    expect(sent).toEqual([{ command: 'preflight', args: { year: 2023, dayFrom: 2, dayTo: 4 }, afterSeq: 0 }])
    expect(screen.getByTestId('preflight-identity')).toHaveTextContent('Agent identity')
    expect(screen.getByTestId('preflight-store')).toHaveTextContent('missing permission: Microsoft.DocumentDB/databaseAccounts/readMetadata')
  })
})
