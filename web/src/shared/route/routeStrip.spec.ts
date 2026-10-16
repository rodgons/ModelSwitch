import { describe, expect, it } from 'vitest'
import { layoutRoute, type StripAttempt } from './routeStrip'

const t0 = '2026-10-15T12:00:00.000Z'
const at = (ms: number) => new Date(Date.parse(t0) + ms).toISOString()
const attempt = (
  number: number,
  start: number,
  end: number,
  status: string,
  kind = 'primary',
  model = 'openai/gpt-4o-mini',
  errorCode: string | null = null,
): StripAttempt => ({
  attemptNumber: number,
  kind,
  status,
  model,
  startedAt: at(start),
  completedAt: at(end),
  errorCode,
  providerErrorType: null,
})

// design-system.md section 10: the Route Strip's anatomy.
describe('layoutRoute', () => {
  it('places each Attempt on a time axis that starts when the request was received', () => {
    const route = layoutRoute(t0, 2404, [
      attempt(1, 0, 812, 'failed'),
      attempt(2, 878, 1690, 'failed', 'retry'),
      attempt(3, 1750, 2404, 'succeeded', 'fallback', 'ollama/llama3.1:8b'),
    ])

    expect(route.totalMs).toBe(2404)
    expect(route.segments.map((s) => [s.offsetMs, s.durationMs])).toEqual([
      [0, 812],
      [878, 812],
      [1750, 654],
    ])
    expect(route.gaps.map((g) => [g.offsetMs, g.durationMs])).toEqual([
      [812, 66],
      [1690, 60],
    ])
  })

  it('marks a failure followed by another Candidate as a Fallback and the last failure as final', () => {
    const fellBack = layoutRoute(t0, 300, [
      attempt(1, 0, 100, 'failed'),
      attempt(2, 100, 300, 'succeeded', 'fallback'),
    ])
    const final = layoutRoute(t0, 300, [
      attempt(1, 0, 100, 'failed'),
      attempt(2, 100, 300, 'failed', 'retry'),
    ])

    expect(fellBack.segments.map((s) => [s.outcome, s.glyph])).toEqual([
      ['fellBack', '⤳'],
      ['succeeded', '✓'],
    ])

    // Retried, then fell back: only the Attempt right before the Fallback fell back.
    const retried = layoutRoute(t0, 300, [
      attempt(1, 0, 10, 'failed'),
      attempt(2, 20, 30, 'failed', 'retry'),
      attempt(3, 40, 300, 'succeeded', 'fallback'),
    ])
    expect(retried.segments.map((s) => s.outcome)).toEqual(['failed', 'fellBack', 'succeeded'])
    expect(final.segments.map((s) => s.outcome)).toEqual(['failed', 'failed'])
    expect(final.segments.map((s) => s.glyph)).toEqual(['✕', '✕'])
  })

  it('shows a timeout with its own glyph and a skipped circuit as a zero-length marker', () => {
    const route = layoutRoute(t0, 1200, [
      attempt(1, 0, 0, 'skipped_circuit_open'),
      attempt(2, 0, 1000, 'ambiguous', 'fallback', 'm', 'deadline_exceeded'),
    ])

    expect(route.segments.map((s) => [s.outcome, s.glyph, s.durationMs])).toEqual([
      ['skipped', '⊘', 0],
      ['timedOut', '◷', 1000],
    ])
  })

  it('gives every segment an accessible name that says what happened', () => {
    const route = layoutRoute(t0, 900, [
      attempt(1, 0, 812, 'failed'),
      attempt(2, 812, 900, 'succeeded', 'fallback', 'ollama/llama3.1:8b'),
    ])

    expect(route.segments[0]!.accessibleName).toBe(
      'Attempt 1, openai/gpt-4o-mini, failed after 812 ms, fell back',
    )
    expect(route.segments[1]!.accessibleName).toBe(
      'Attempt 2, ollama/llama3.1:8b, succeeded after 88 ms',
    )
  })

  it('labels a segment only when its label fits before the next one, and always the last', () => {
    const route = layoutRoute(t0, 1000, [
      attempt(1, 0, 10, 'failed'),
      attempt(2, 500, 510, 'failed', 'retry'),
      attempt(3, 560, 1000, 'succeeded', 'fallback'),
    ])
    expect(route.segments.map((s) => s.labelled)).toEqual([true, false, true])
  })

  it('uses the last Attempt end when the request has no duration yet', () => {
    expect(layoutRoute(t0, null, [attempt(1, 0, 500, 'succeeded')]).totalMs).toBe(500)
  })
})
