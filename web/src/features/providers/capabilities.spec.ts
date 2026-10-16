import { describe, expect, it } from 'vitest'
import { fromForm, summarize, toForm } from './capabilities'

describe('capability editor', () => {
  const stored = {
    streaming: true,
    jsonObject: true,
    jsonSchema: false,
    contextWindow: 128000,
    maxOutputTokens: 16384,
    parameters: ['temperature', 'seed'],
    tokenizer: 'o200k_base',
  }

  it('round-trips a stored capabilities document', () => {
    expect(fromForm(toForm(stored))).toEqual(stored)
  })

  it('leaves out empty fields instead of sending zero', () => {
    expect(fromForm({ ...toForm({}), streaming: true })).toEqual({
      streaming: true,
      jsonObject: false,
      jsonSchema: false,
    })
  })

  it('summarizes for the table', () => {
    expect(summarize(stored)).toBe('Streaming · JSON object · 128,000 ctx')
    expect(summarize(null)).toBe('No Capabilities declared')
  })
})
