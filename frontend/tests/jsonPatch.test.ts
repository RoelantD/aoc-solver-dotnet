import { describe, expect, it } from 'vitest'
import { PatchError, applyPatch } from '../src/lib/jsonPatch'

describe('RFC 6902 patch applier', () => {
  const doc = {
    run: { status: 'running', id: 'r1' },
    calendar: [
      ['NOT_STARTED', 'NOT_STARTED'],
      ['NOT_STARTED', 'NOT_STARTED'],
    ],
    usage: { totals: { calls: 0 } },
  }

  it('applies the paths the agent emits without mutating the input', () => {
    const next = applyPatch(doc, [
      { op: 'replace', path: '/run/status', value: 'completed' },
      { op: 'replace', path: '/calendar/1/0', value: 'VERIFIED' },
      { op: 'replace', path: '/usage', value: { totals: { calls: 3 } } },
      { op: 'replace', path: '/stars', value: 1 },
    ])
    expect(next.run.status).toBe('completed')
    expect(next.calendar[1][0]).toBe('VERIFIED')
    expect(next.usage.totals.calls).toBe(3)
    expect((next as unknown as { stars: number }).stars).toBe(1)
    // structural sharing: untouched branches keep their identity, the input is unchanged
    expect(next.calendar[0]).toBe(doc.calendar[0])
    expect(doc.run.status).toBe('running')
    expect(doc.calendar[1][0]).toBe('NOT_STARTED')
  })

  it('treats replace of an omitted (null) member as add, e.g. /activeAgent', () => {
    const next = applyPatch(doc as Record<string, unknown>, [{ op: 'replace', path: '/activeAgent', value: 'coder' }])
    expect(next.activeAgent).toBe('coder')
    expect(applyPatch(next, [{ op: 'replace', path: '/activeAgent', value: null }]).activeAgent).toBeNull()
  })

  it('supports add, remove, move, copy, test and escaped pointers', () => {
    let d: Record<string, unknown> = { a: [1, 2], 'x/y': { '~k': 1 } }
    d = applyPatch(d, [
      { op: 'add', path: '/a/-', value: 3 },
      { op: 'add', path: '/a/0', value: 0 },
      { op: 'remove', path: '/a/1' },
      { op: 'copy', from: '/a', path: '/b' },
      { op: 'move', from: '/x~1y/~0k', path: '/k' },
      { op: 'test', path: '/k', value: 1 },
    ])
    expect(d).toEqual({ a: [0, 2, 3], b: [0, 2, 3], 'x/y': {}, k: 1 })
  })

  it('throws on invalid paths and failed tests', () => {
    expect(() => applyPatch(doc, [{ op: 'replace', path: '/calendar/9/0', value: 'X' }])).toThrow(PatchError)
    expect(() => applyPatch(doc, [{ op: 'remove', path: '/nope' }])).toThrow(PatchError)
    expect(() => applyPatch(doc, [{ op: 'test', path: '/run/status', value: 'paused' }])).toThrow(PatchError)
  })
})
