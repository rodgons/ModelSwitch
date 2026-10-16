import { describe, expect, it } from 'vitest'
import { compactJson, prettyJson } from './json'

describe('case JSON display', () => {
  it('shows short values whole on one line', () => {
    expect(compactJson({ a: 1, b: 'x' })).toEqual({ text: '{"a":1,"b":"x"}', truncated: false })
  })

  it('truncates long values with an ellipsis', () => {
    const shown = compactJson({ text: 'y'.repeat(200) }, 20)
    expect(shown.truncated).toBe(true)
    expect(shown.text).toHaveLength(20)
    expect(shown.text.endsWith('…')).toBe(true)
  })

  it('shows a missing value as a dash', () => {
    expect(compactJson(null)).toEqual({ text: '—', truncated: false })
    expect(compactJson(undefined)).toEqual({ text: '—', truncated: false })
  })

  it('pretty-prints with two-space indentation', () => {
    expect(prettyJson({ a: [1] })).toBe('{\n  "a": [\n    1\n  ]\n}')
  })
})
