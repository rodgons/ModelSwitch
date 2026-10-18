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
  /** Where the segment is drawn, as shares of the strip's width (see `layoutRoute`). */
  x: number
  w: number
  /** The room the model label has: from `labelStart`, `labelWidth` wide, as shares of the strip's width. */
  labelStart: number
  labelWidth: number
}

export interface StripGap {
  offsetMs: number
  durationMs: number
  x: number
  w: number
  /** Drawn shorter than the wait it stands for, with an axis break and its real duration. */
  shortened: boolean
}

export interface StripTick {
  at: number
  label: string
}

export interface RouteLayout {
  totalMs: number
  segments: StripSegment[]
  gaps: StripGap[]
  /** False when a segment was widened or a wait shortened so that every Attempt stays legible. */
  toScale: boolean
  ticks: StripTick[]
}

/** How far the strip may bend the time axis, as shares of its width. */
export interface StripScale {
  /** The narrowest a segment is drawn, so its end glyph never sits on its neighbour's. */
  minSegment: number
  /** The narrowest a wait between two Attempts is drawn, so two segments never touch. */
  minGap: number
  /** The widest a wait is drawn. A longer wait is shortened, marked with a break and labelled with its real duration. */
  maxGap: number
}

export const fullScale: StripScale = { minSegment: 0.06, minGap: 0.02, maxGap: 0.25 }
export const miniScale: StripScale = { minSegment: 0.08, minGap: 0.05, maxGap: 0.3 }

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
 *
 * The axis is to scale whenever that keeps every Attempt legible. A backoff can be fifty times longer than the Attempts around it,
 * which on a true scale crushes them into one end of the strip, so `scale` bends it: a segment is drawn at least `minSegment` wide
 * and a wait at most `maxGap`. Bent axes say so: `toScale` is false, shortened waits carry a break, and the ticks name real times.
 */
export function layoutRoute(
  requestStartedAt: string,
  requestDurationMs: number | null,
  attempts: StripAttempt[],
  scale: StripScale = fullScale,
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
      x: 0,
      w: 0,
      labelStart: 0,
      labelWidth: 0,
    }
  })

  const gaps: StripGap[] = []
  for (let i = 1; i < segments.length; i++) {
    const previousEnd = segments[i - 1]!.offsetMs + segments[i - 1]!.durationMs
    const wait = segments[i]!.offsetMs - previousEnd
    if (wait > 0)
      gaps.push({ offsetMs: previousEnd, durationMs: wait, x: 0, w: 0, shortened: false })
  }

  const lastEnd = segments.reduce((max, s) => Math.max(max, s.offsetMs + s.durationMs), 0)
  const totalMs = Math.max(requestDurationMs ?? 0, lastEnd, 1)

  // Weights in milliseconds of drawn axis: real durations, widened or shortened within the scale's bounds. The bounds are shares of
  // the drawn axis, which itself depends on the bending, so the layout settles over a few passes.
  type Piece = { kind: 'lead' | 'segment' | 'gap' | 'tail'; index: number; weight: number }
  let toScale = true
  let pieces: Piece[] = []
  let drawnTotal = totalMs
  for (let pass = 0; pass < 4; pass++) {
    const reference = drawnTotal
    toScale = true
    const bend = (ms: number, min: number, max = Infinity) => {
      const drawn = Math.min(Math.max(ms, min * reference), max * reference)
      if (Math.abs(drawn - ms) > reference * 0.005) toScale = false
      return drawn
    }
    pieces = [{ kind: 'lead', index: 0, weight: segments[0]?.offsetMs ?? 0 }]
    segments.forEach((segment, index) => {
      if (index > 0) {
        const gapIndex = gaps.findIndex((g) => g.offsetMs + g.durationMs === segment.offsetMs)
        const wait = gapIndex >= 0 ? gaps[gapIndex]!.durationMs : 0
        // Overlapping Attempts (a hedge, or clock skew) are drawn side by side: the strip shows order, the table the exact times.
        pieces.push({
          kind: 'gap',
          index: gapIndex,
          weight: bend(wait, scale.minGap, scale.maxGap),
        })
        if (gapIndex >= 0) gaps[gapIndex]!.shortened = wait > scale.maxGap * reference
      }
      pieces.push({ kind: 'segment', index, weight: bend(segment.durationMs, scale.minSegment) })
    })
    pieces.push({ kind: 'tail', index: 0, weight: Math.max(0, totalMs - lastEnd) })
    drawnTotal = pieces.reduce((sum, p) => sum + p.weight, 0) || 1
  }
  let cursor = 0
  for (const piece of pieces) {
    const x = cursor / drawnTotal
    const w = piece.weight / drawnTotal
    if (piece.kind === 'segment') Object.assign(segments[piece.index]!, { x, w })
    if (piece.kind === 'gap' && piece.index >= 0) Object.assign(gaps[piece.index]!, { x, w })
    cursor += piece.weight
  }

  // Labels: each runs from its segment to the next one; the last is right-aligned and keeps at least `labelRoom`, taking it from
  // the labels before it.
  const last = segments.at(-1)
  const lastStart = last ? Math.min(last.x, 1 - labelRoom) : 1
  segments.forEach((segment, index) => {
    if (segment === last) {
      Object.assign(segment, { labelled: true, labelStart: lastStart, labelWidth: 1 - lastStart })
      return
    }
    const end = Math.min(segments[index + 1]!.x, lastStart)
    const width = end - segment.x
    Object.assign(segment, {
      labelled: width >= labelRoom,
      labelStart: segment.x,
      labelWidth: width,
    })
  })

  const ticks: StripTick[] = toScale
    ? [0, 0.25, 0.5, 0.75, 1].map((at) => ({ at, label: formatDuration(Math.round(totalMs * at)) }))
    : [
        { at: 0, label: formatDuration(0) },
        ...gaps
          .filter((g) => g.shortened && g.w >= labelRoom)
          .map((g) => ({ at: g.x + g.w / 2, label: `${formatDuration(g.durationMs)} wait` })),
        { at: 1, label: formatDuration(totalMs) },
      ]

  return { totalMs, segments, gaps, toScale, ticks }
}
