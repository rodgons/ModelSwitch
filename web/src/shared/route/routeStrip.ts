import { formatDuration } from '@/shared/format'

/** What the strip needs of one Attempt (a subset of the request detail's Attempt timeline). */
export interface StripAttempt {
  attemptNumber: number
  kind: string
  status: string
  model: string
  startedAt: string
  completedAt: string
  errorCode: string | null
  providerErrorType: string | null
}

export type Outcome = 'succeeded' | 'fellBack' | 'failed' | 'timedOut' | 'skipped'

export interface StripSegment {
  attemptNumber: number
  model: string
  offsetMs: number
  durationMs: number
  outcome: Outcome
  glyph: string
  accessibleName: string
  /** Whether the full strip draws this segment's model label: only when it fits before the next one; the last is always drawn. */
  labelled: boolean
}

export interface StripGap {
  offsetMs: number
  durationMs: number
}

export interface RouteLayout {
  totalMs: number
  segments: StripSegment[]
  gaps: StripGap[]
}

/** The share of the axis a label needs before the next segment starts. */
const labelRoom = 0.12

const glyphs: Record<Outcome, string> = {
  succeeded: '✓',
  fellBack: '⤳',
  failed: '✕',
  timedOut: '◷',
  skipped: '⊘',
}
const words: Record<Outcome, string> = {
  succeeded: 'succeeded',
  fellBack: 'failed',
  failed: 'failed',
  timedOut: 'timed out',
  skipped: 'skipped because its circuit was open',
}

function outcomeOf(attempt: StripAttempt, later: StripAttempt[]): Outcome {
  if (attempt.status === 'succeeded') return 'succeeded'
  if (attempt.status === 'skipped_circuit_open') return 'skipped'
  // "Fell back": it failed and the next Attempt that ran was on another Candidate (a Fallback). A failure followed by a Retry did not.
  if (later.find((a) => a.status !== 'skipped_circuit_open')?.kind === 'fallback') return 'fellBack'
  const timedOut =
    attempt.errorCode === 'deadline_exceeded' ||
    (attempt.providerErrorType ?? '').includes('timeout')
  return timedOut ? 'timedOut' : 'failed'
}

/**
 * Lays out one request's Attempts for the Route Strip (design-system.md section 10): segment offsets and lengths on a time axis that
 * starts when the request was received, the waits between Attempts, and an outcome per segment that is never shown by color alone.
 */
export function layoutRoute(
  requestStartedAt: string,
  requestDurationMs: number | null,
  attempts: StripAttempt[],
): RouteLayout {
  const start = Date.parse(requestStartedAt)
  const ordered = [...attempts].sort((a, b) => a.attemptNumber - b.attemptNumber)
  const segments = ordered.map((attempt, index) => {
    const offsetMs = Math.max(0, Date.parse(attempt.startedAt) - start)
    const durationMs = Math.max(0, Date.parse(attempt.completedAt) - Date.parse(attempt.startedAt))
    const outcome = outcomeOf(attempt, ordered.slice(index + 1))
    const verdict =
      outcome === 'skipped'
        ? words.skipped
        : `${words[outcome]} after ${formatDuration(durationMs)}`
    return {
      attemptNumber: attempt.attemptNumber,
      model: attempt.model,
      offsetMs,
      durationMs,
      outcome,
      glyph: glyphs[outcome],
      accessibleName: `Attempt ${attempt.attemptNumber}, ${attempt.model}, ${verdict}${outcome === 'fellBack' ? ', fell back' : ''}`,
      labelled: true,
    }
  })

  const gaps: StripGap[] = []
  for (let i = 1; i < segments.length; i++) {
    const previousEnd = segments[i - 1]!.offsetMs + segments[i - 1]!.durationMs
    const wait = segments[i]!.offsetMs - previousEnd
    if (wait > 0) gaps.push({ offsetMs: previousEnd, durationMs: wait })
  }

  const lastEnd = segments.reduce((max, s) => Math.max(max, s.offsetMs + s.durationMs), 0)
  const totalMs = Math.max(requestDurationMs ?? 0, lastEnd, 1)
  for (let i = 0; i < segments.length - 1; i++) {
    segments[i]!.labelled =
      (segments[i + 1]!.offsetMs - segments[i]!.offsetMs) / totalMs >= labelRoom
  }

  return { totalMs, segments, gaps }
}
