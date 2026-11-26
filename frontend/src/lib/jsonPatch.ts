// Minimal RFC 6902 JSON Patch applier for AG-UI STATE_DELTA events. Immutable: returns a new document and only
// copies the containers along each patched path, so React sees changed references exactly where state changed.
//
// One deliberate leniency: `replace` on a missing object member behaves like `add`. The agent serializes snapshots
// with null members omitted (e.g. `activeAgent`), while its deltas `replace` those paths.

export interface PatchOperation {
  op: 'add' | 'remove' | 'replace' | 'move' | 'copy' | 'test'
  path: string
  value?: unknown
  from?: string
}

export class PatchError extends Error {}

type Container = Record<string, unknown> | unknown[]

/** Splits a JSON Pointer (RFC 6901) into unescaped tokens: `~1` is `/`, `~0` is `~`. */
function parsePointer(pointer: string): string[] {
  if (pointer === '') return []
  if (!pointer.startsWith('/')) throw new PatchError(`invalid JSON pointer '${pointer}'`)
  return pointer
    .slice(1)
    .split('/')
    .map((t) => t.replace(/~1/g, '/').replace(/~0/g, '~'))
}

function isContainer(v: unknown): v is Container {
  return typeof v === 'object' && v !== null
}

function shallowCopy(v: Container): Container {
  return Array.isArray(v) ? [...v] : { ...v }
}

/** Validates an array index token; `-` (append) is only valid for inserts. */
function arrayIndex(arr: unknown[], token: string, forInsert: boolean): number {
  if (forInsert && token === '-') return arr.length
  if (!/^(0|[1-9]\d*)$/.test(token)) throw new PatchError(`invalid array index '${token}'`)
  const i = Number(token)
  if (i > arr.length || (!forInsert && i >= arr.length)) throw new PatchError(`array index ${i} out of range`)
  return i
}

function getAt(doc: unknown, pointer: string): unknown {
  let cur: unknown = doc
  for (const token of parsePointer(pointer)) {
    if (Array.isArray(cur)) cur = cur[arrayIndex(cur, token, false)]
    else if (isContainer(cur) && Object.prototype.hasOwnProperty.call(cur, token)) cur = (cur as Record<string, unknown>)[token]
    else throw new PatchError(`path '${pointer}' does not exist`)
  }
  return cur
}

/** Rebuilds the containers along `tokens` and lets `mutate` change the (already copied) parent of the last token. */
function update(doc: unknown, tokens: string[], mutate: (parent: Container, last: string) => void): unknown {
  if (!isContainer(doc)) throw new PatchError('cannot patch inside a non-container value')
  const root = shallowCopy(doc)
  let parent: Container = root
  for (let i = 0; i < tokens.length - 1; i++) {
    const t = tokens[i]
    const child: unknown = Array.isArray(parent)
      ? parent[arrayIndex(parent, t, false)]
      : Object.prototype.hasOwnProperty.call(parent, t)
        ? (parent as Record<string, unknown>)[t]
        : undefined
    if (!isContainer(child)) throw new PatchError(`path segment '${t}' does not exist`)
    const copy = shallowCopy(child)
    if (Array.isArray(parent)) parent[Number(t)] = copy
    else (parent as Record<string, unknown>)[t] = copy
    parent = copy
  }
  mutate(parent, tokens[tokens.length - 1])
  return root
}

function add(doc: unknown, path: string, value: unknown): unknown {
  const tokens = parsePointer(path)
  if (!tokens.length) return value
  return update(doc, tokens, (parent, last) => {
    if (Array.isArray(parent)) parent.splice(arrayIndex(parent, last, true), 0, value)
    else parent[last] = value
  })
}

function remove(doc: unknown, path: string): unknown {
  const tokens = parsePointer(path)
  if (!tokens.length) throw new PatchError('cannot remove the document root')
  return update(doc, tokens, (parent, last) => {
    if (Array.isArray(parent)) parent.splice(arrayIndex(parent, last, false), 1)
    else if (Object.prototype.hasOwnProperty.call(parent, last)) delete parent[last]
    else throw new PatchError(`path '${path}' does not exist`)
  })
}

function replace(doc: unknown, path: string, value: unknown): unknown {
  const tokens = parsePointer(path)
  if (!tokens.length) return value
  return update(doc, tokens, (parent, last) => {
    if (Array.isArray(parent)) parent[arrayIndex(parent, last, false)] = value
    else parent[last] = value
  })
}

/** Structural equality for the `test` operation. */
function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true
  if (!isContainer(a) || !isContainer(b) || Array.isArray(a) !== Array.isArray(b)) return false
  const ka = Object.keys(a)
  const kb = Object.keys(b)
  if (ka.length !== kb.length) return false
  return ka.every((k) => deepEqual((a as Record<string, unknown>)[k], (b as Record<string, unknown>)[k]))
}

function applyOperation<T>(doc: T, op: PatchOperation): T {
  switch (op.op) {
    case 'add':
      return add(doc, op.path, op.value) as T
    case 'remove':
      return remove(doc, op.path) as T
    case 'replace':
      return replace(doc, op.path, op.value) as T
    case 'copy':
      return add(doc, op.path, structuredClone(getAt(doc, op.from ?? ''))) as T
    case 'move': {
      const value = getAt(doc, op.from ?? '')
      return add(remove(doc, op.from ?? ''), op.path, value) as T
    }
    case 'test':
      if (!deepEqual(getAt(doc, op.path), op.value)) throw new PatchError(`test failed at '${op.path}'`)
      return doc
    default:
      throw new PatchError(`unknown op '${(op as { op: string }).op}'`)
  }
}

/**
 * Applies all operations in order. Because nothing is mutated, a failing operation leaves the original document
 * untouched: the error is thrown and the caller keeps its old state.
 */
export function applyPatch<T>(doc: T, patch: readonly PatchOperation[]): T {
  return patch.reduce((d, op) => applyOperation(d, op), doc)
}
