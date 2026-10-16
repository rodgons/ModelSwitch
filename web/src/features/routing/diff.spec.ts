import { describe, expect, it } from 'vitest'
import { diffLines, pretty } from './diff'

describe('diffLines', () => {
  it('marks removed and added lines around the unchanged ones', () => {
    expect(diffLines('a\nb\nc', 'a\nx\nc')).toEqual([
      { kind: 'same', text: 'a' },
      { kind: 'removed', text: 'b' },
      { kind: 'added', text: 'x' },
      { kind: 'same', text: 'c' },
    ])
  })

  it('handles appended and dropped tails', () => {
    expect(diffLines('a', 'a\nb').map((l) => l.kind)).toEqual(['same', 'added'])
    expect(diffLines('a\nb', 'a').map((l) => l.kind)).toEqual(['same', 'removed'])
  })

  it('pretty-prints definitions with two spaces', () => {
    expect(pretty({ strategy: 'static' })).toBe('{\n  "strategy": "static"\n}')
  })
})
