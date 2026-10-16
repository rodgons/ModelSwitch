import { describe, expect, it } from 'vitest'
import {
  caseChoices,
  costAxisMax,
  describeVariance,
  diffOutputs,
  pivotCase,
  pollInterval,
  scatterPoints,
} from './comparison'

describe('live progress polling', () => {
  it('polls while any run is queued or running, and stops when all have finished', () => {
    expect(pollInterval(['succeeded', 'running'])).toBe(2000)
    expect(pollInterval(['queued'])).toBe(2000)
    expect(pollInterval(['succeeded', 'failed', 'cancelled'])).toBe(false)
    expect(pollInterval([])).toBe(false)
  })
})

describe('quality vs cost scatter', () => {
  const run = (label: string, quality: number | null, cost: number | null) => ({
    runId: label,
    target: { label },
    quality: quality === null ? null : { metric: 'accuracy', value: quality },
    candidateCostPerCase: cost,
  })

  it('plots each run with both a quality and a per-Case candidate cost', () => {
    expect(
      scatterPoints([run('a', 0.9, 0.002), run('b', null, 0.001), run('c', 0.5, null)]),
    ).toEqual([{ runId: 'a', label: 'a', cost: 0.002, quality: 0.9 }])
  })

  it('starts the cost axis at zero and leaves headroom above the dearest run', () => {
    expect(costAxisMax([0.001, 0.004])).toBeCloseTo(0.0046)
    expect(costAxisMax([0, 0])).toBe(0.0001)
    expect(costAxisMax([])).toBe(0.0001)
  })
})

describe('run-to-run variance', () => {
  it('describes the spread from two runs of the same configuration', () => {
    expect(describeVariance({ runs: 3, min: 0.8, max: 0.9, mean: 0.85, stdDev: 0.05 })).toBe(
      '3 runs: mean 0.850, standard deviation 0.050, range 0.800–0.900',
    )
  })

  it('has nothing to say for a single run', () => {
    expect(describeVariance(null)).toBeNull()
    expect(describeVariance({ runs: 1, min: 0.8, max: 0.8, mean: 0.8, stdDev: 0 })).toBeNull()
  })
})

describe('Case drill-down', () => {
  const result = (externalId: string, ordinal: number, outputText = '') => ({
    externalId,
    ordinal,
    outputText,
  })

  it('lists every Case any run has a result for, in upload order', () => {
    expect(
      caseChoices([
        { runId: 'r1', results: [result('c-2', 1), result('c-1', 0)] },
        { runId: 'r2', results: [result('c-3', 2), result('c-1', 0)] },
      ]),
    ).toEqual(['c-1', 'c-2', 'c-3'])
  })

  it('lines up one Case across runs, with a gap where a run has no result yet', () => {
    const pivot = pivotCase(
      [
        { runId: 'r1', results: [result('c-1', 0, 'yes')] },
        { runId: 'r2', results: [] },
      ],
      'c-1',
    )
    expect(pivot).toEqual([
      { runId: 'r1', result: result('c-1', 0, 'yes') },
      { runId: 'r2', result: null },
    ])
  })
})

describe('output diff between two runs', () => {
  it('diffs JSON outputs field by field after pretty-printing', () => {
    const diff = diffOutputs('{"name":"Ana","email":"a@x"}', '{"email":"a@x", "name":"Bob"}')
    expect(diff.mode).toBe('lines')
    expect(diff.parts).toContainEqual({ kind: 'same', text: '  "email": "a@x",' })
    expect(diff.parts).toContainEqual({ kind: 'removed', text: '  "name": "Ana"' })
    expect(diff.parts).toContainEqual({ kind: 'added', text: '  "name": "Bob"' })
  })

  it('diffs prose word by word, keeping the spacing', () => {
    const diff = diffOutputs('the cat sat', 'the dog sat')
    expect(diff.mode).toBe('words')
    expect(diff.parts).toEqual([
      { kind: 'same', text: 'the ' },
      { kind: 'removed', text: 'cat' },
      { kind: 'added', text: 'dog' },
      { kind: 'same', text: ' sat' },
    ])
  })

  it('treats a missing output as empty', () => {
    expect(diffOutputs(null, 'hi').parts).toEqual([{ kind: 'added', text: 'hi' }])
  })
})
