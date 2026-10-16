import { diffLines } from '@/features/routing/diff'
import { formatScore } from '@/shared/format'

/** A run still has work to do: its views poll for progress. */
export const activeStatuses = new Set(['queued', 'running'])

/** How often a view refetches while any of its runs is queued or running (ms), or false once every run has finished. */
export function pollInterval(statuses: readonly string[]): number | false {
  return statuses.some((s) => activeStatuses.has(s)) ? 2000 : false
}

export interface ScatterPoint {
  runId: string
  label: string
  cost: number
  quality: number
}

/** Quality against per-Case candidate Estimated Cost, the comparable number (evaluation.md section 3); runs missing either are left out. */
export function scatterPoints(
  runs: readonly {
    runId: string
    target: { label: string }
    quality: { value: number | null } | null
    candidateCostPerCase: number | null
  }[],
): ScatterPoint[] {
  return runs.flatMap((r) =>
    r.quality?.value !== null && r.quality?.value !== undefined && r.candidateCostPerCase !== null
      ? [
          {
            runId: r.runId,
            label: r.target.label,
            cost: r.candidateCostPerCase,
            quality: r.quality.value,
          },
        ]
      : [],
  )
}

/** The cost axis runs from 0 to 15 % above the dearest run, so no point sits on the edge; a tiny range when every cost is 0. */
export function costAxisMax(costs: readonly number[]): number {
  const max = Math.max(0, ...costs)
  return max > 0 ? max * 1.15 : 0.0001
}

/** Run-to-run variance of the quality value over runs of the same configuration (evaluation.md section 6), from two runs. */
export function describeVariance(
  variance: { runs: number; min: number; max: number; mean: number; stdDev: number } | null,
): string | null {
  if (!variance || variance.runs < 2) return null
  return `${variance.runs} runs: mean ${formatScore(variance.mean)}, standard deviation ${formatScore(variance.stdDev)}, range ${formatScore(variance.min)}–${formatScore(variance.max)}`
}

export interface DiffPart {
  kind: 'same' | 'added' | 'removed'
  text: string
}

const linePrefixes: Record<DiffPart['kind'], string> = { added: '+', removed: '-', same: ' ' }

/** A line diff's gutter, as in a unified diff: `+` added, `-` removed, a space for an unchanged line. */
export function diffLinePrefix(kind: DiffPart['kind']): string {
  return linePrefixes[kind]
}

function parseJson(text: string): unknown {
  try {
    const value: unknown = JSON.parse(text)
    return typeof value === 'object' && value !== null ? value : undefined
  } catch {
    return undefined
  }
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys)
  if (typeof value !== 'object' || value === null) return value
  return Object.fromEntries(
    Object.keys(value)
      .sort()
      .map((k) => [k, sortKeys((value as Record<string, unknown>)[k])]),
  )
}

/** Longest-common-subsequence diff over tokens, merging neighbours of the same kind. */
function diffTokens(a: string[], b: string[]): DiffPart[] {
  const lcs: number[][] = Array.from({ length: a.length + 1 }, () =>
    new Array<number>(b.length + 1).fill(0),
  )
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      lcs[i]![j] =
        a[i] === b[j] ? lcs[i + 1]![j + 1]! + 1 : Math.max(lcs[i + 1]![j]!, lcs[i]![j + 1]!)
  const parts: DiffPart[] = []
  const push = (kind: DiffPart['kind'], text: string) => {
    const last = parts[parts.length - 1]
    if (last?.kind === kind) last.text += text
    else parts.push({ kind, text })
  }
  let i = 0
  let j = 0
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      push('same', a[i]!)
      i++
      j++
    } else if (lcs[i + 1]![j]! >= lcs[i]![j + 1]!) push('removed', a[i++]!)
    else push('added', b[j++]!)
  }
  while (i < a.length) push('removed', a[i++]!)
  while (j < b.length) push('added', b[j++]!)
  return parts
}

const words = (text: string) => text.match(/\s+|[^\s]+/g) ?? []

/**
 * Two runs' outputs for one Case side by side: JSON objects are compared field by field (keys sorted, pretty-printed, by line),
 * anything else word by word.
 */
export function diffOutputs(
  before: string | null,
  after: string | null,
): { mode: 'lines' | 'words'; parts: DiffPart[] } {
  const a = parseJson(before ?? '')
  const b = parseJson(after ?? '')
  if (a !== undefined && b !== undefined) {
    const pretty = (v: unknown) => JSON.stringify(sortKeys(v), null, 2)
    return { mode: 'lines', parts: diffLines(pretty(a), pretty(b)) }
  }
  return { mode: 'words', parts: diffTokens(words(before ?? ''), words(after ?? '')) }
}
