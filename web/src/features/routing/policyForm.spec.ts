import { describe, expect, it } from 'vitest'
import { readForm, writeForm } from './policyForm'

describe('policy form view', () => {
  const definition = {
    strategy: 'rules',
    candidates: ['openai/gpt-4o-mini', 'ollama/llama3.1:8b'],
    rules: [{ when: { taskType: ['extraction'] }, candidates: ['openai/gpt-4o-mini'] }],
    execution: {
      deadlineMs: 60000,
      maxAttempts: 4,
      backoff: { baseMs: 200, maxMs: 2000, jitter: 'full' },
    },
  }

  it('reads the common fields', () => {
    expect(readForm(definition)).toEqual({
      strategy: 'rules',
      candidates: 'openai/gpt-4o-mini\nollama/llama3.1:8b',
      deadlineMs: '60000',
      attemptTimeoutMs: '',
      maxAttempts: '4',
      maxRequestCost: '',
    })
  })

  it('writes them back without touching rules or backoff', () => {
    const written = writeForm(definition, {
      ...readForm(definition),
      candidates: 'ollama/llama3.1:8b',
      attemptTimeoutMs: '20000',
      maxRequestCost: '0.05',
    })

    expect(written.candidates).toEqual(['ollama/llama3.1:8b'])
    expect(written.rules).toEqual(definition.rules)
    expect(written.execution).toEqual({
      deadlineMs: 60000,
      maxAttempts: 4,
      attemptTimeoutMs: 20000,
      backoff: definition.execution.backoff,
    })
    expect(written.maxRequestCost).toBe('0.05')
  })

  it('removes a key whose field was emptied', () => {
    expect(
      writeForm({ execution: { deadlineMs: 1 } }, { ...readForm({}), deadlineMs: '' }).execution,
    ).toBeUndefined()
  })
})
