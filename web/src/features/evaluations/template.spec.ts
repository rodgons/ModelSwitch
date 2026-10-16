import { describe, expect, it } from 'vitest'
import { detectVariables, moveItem, parseResponseFormat, readTemplateErrors } from './template'

describe('prompt template variables', () => {
  it('finds each {{variable}} once, in order of first use, across messages', () => {
    expect(
      detectVariables([
        { role: 'system', content: 'Reply in {{ lang }}.' },
        { role: 'user', content: '{{text}} and {{lang}} and {{ text }} {{_x1}}' },
      ]),
    ).toEqual(['lang', 'text', '_x1'])
  })

  it('ignores placeholders that are not identifiers, like the server does', () => {
    expect(
      detectVariables([{ role: 'user', content: '{{1x}} {{a-b}} {{}} {x} {{ ok }}' }]),
    ).toEqual(['ok'])
  })
})

describe('prompt response format', () => {
  it('is absent when blank', () => {
    expect(parseResponseFormat('  ')).toEqual({ ok: true, value: null })
  })

  it('parses a JSON object', () => {
    expect(parseResponseFormat('{"type":"json_object"}')).toEqual({
      ok: true,
      value: { type: 'json_object' },
    })
  })

  it('rejects invalid JSON and anything that is not an object', () => {
    expect(parseResponseFormat('{type')).toMatchObject({ ok: false })
    expect(parseResponseFormat('[1]')).toMatchObject({ ok: false })
    expect(parseResponseFormat('"text"')).toMatchObject({ ok: false })
  })
})

describe('prompt message reordering', () => {
  it('moves an item up or down and leaves the list alone at the ends', () => {
    expect(moveItem(['a', 'b', 'c'], 2, -1)).toEqual(['a', 'c', 'b'])
    expect(moveItem(['a', 'b', 'c'], 0, 1)).toEqual(['b', 'a', 'c'])
    expect(moveItem(['a', 'b'], 0, -1)).toEqual(['a', 'b'])
    expect(moveItem(['a', 'b'], 1, 1)).toEqual(['a', 'b'])
  })
})

describe('prompt template validation errors', () => {
  it('places message errors on the message by 0-based index', () => {
    const read = readTemplateErrors({
      'messages[1].content': ['Must not be blank.'],
      'messages[0].role': ['Must be system, user or assistant.'],
    })
    expect(read.message(1)).toEqual({ role: [], content: ['Must not be blank.'] })
    expect(read.message(0).role).toEqual(['Must be system, user or assistant.'])
    expect(read.message(5)).toEqual({ role: [], content: [] })
    expect(read.general).toEqual([])
  })

  it('collects responseFormat and its fields together', () => {
    const read = readTemplateErrors({
      responseFormat: ['Unknown type.'],
      'responseFormat.name': ['Too long.'],
    })
    expect(read.responseFormat).toEqual(['Unknown type.', 'name: Too long.'])
  })

  it('shows errors about the message list and unknown keys as general', () => {
    const read = readTemplateErrors({ messages: ['At most 256.'], body: ['Bad.'] })
    expect(read.general).toEqual(['At most 256.', 'body: Bad.'])
  })
})
