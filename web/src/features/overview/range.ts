/** The Overview's time ranges (frontend.md section 2): fixed spans ending now, or a custom from/to. */
export type RangeKey = '1h' | '24h' | '7d' | '30d' | 'custom'

const spans: Record<Exclude<RangeKey, 'custom'>, number> = {
  '1h': 3_600_000,
  '24h': 86_400_000,
  '7d': 7 * 86_400_000,
  '30d': 30 * 86_400_000,
}

export function rangeFor(
  key: RangeKey,
  now: Date,
  custom?: { from: string; to: string },
): { from: string; to: string } {
  if (key === 'custom' && custom?.from && custom.to)
    return {
      from: new Date(`${custom.from}Z`).toISOString(),
      to: new Date(`${custom.to}Z`).toISOString(),
    }
  const span = spans[key === 'custom' ? '24h' : key]
  return { from: new Date(now.getTime() - span).toISOString(), to: now.toISOString() }
}

/** The period of equal length just before a range, for the Readout's deltas. */
export function previousRange(range: { from: string; to: string }): { from: string; to: string } {
  const from = Date.parse(range.from)
  const span = Date.parse(range.to) - from
  return { from: new Date(from - span).toISOString(), to: range.from }
}

/** "+12.0 % vs previous period", or null when there is nothing to compare with. */
export function delta(
  current: number | null | undefined,
  previous: number | null | undefined,
): string | null {
  if (
    current === null ||
    current === undefined ||
    previous === null ||
    previous === undefined ||
    previous === 0
  )
    return null
  const change = ((current - previous) / previous) * 100
  return `${change >= 0 ? '+' : ''}${change.toFixed(1)} % vs previous period`
}

/**
 * The Overview series with every step of the range present: the API returns only the buckets that had requests, and a chart of those
 * alone would draw nothing for one bucket and bridge quiet periods with misleading lines. Missing buckets get `empty`.
 */
export function fillBuckets<T>(
  from: string,
  to: string,
  stepSeconds: number,
  points: { start: string; figures: T }[],
  empty: T,
): { start: string; figures: T }[] {
  const step = stepSeconds * 1000
  const byStart = new Map(points.map((p) => [Date.parse(p.start), p.figures]))
  const first = Math.floor(Date.parse(from) / step) * step
  const filled: { start: string; figures: T }[] = []
  for (let t = first; t < Date.parse(to); t += step)
    filled.push({ start: new Date(t).toISOString(), figures: byStart.get(t) ?? empty })
  return filled
}
