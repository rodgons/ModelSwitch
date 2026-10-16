// frontend.md section 3, "Units". Every figure on screen goes through these functions; components never format numbers themselves.

const dash = '—'
const integer = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })

/** Milliseconds below one second, seconds with two decimals from one second. */
export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return dash
  return ms < 1000 ? `${integer.format(Math.round(ms))} ms` : `${(ms / 1000).toFixed(2)} s`
}

/** Token counts with thousands separators: 12,480. */
export function formatTokens(count: number | null | undefined): string {
  return count === null || count === undefined ? dash : integer.format(count)
}

/** USD: four significant decimals below $1, two decimals from $1. Always pair with an "Estimated" label (Money component). */
export function formatMoney(amount: number | null | undefined): string {
  if (amount === null || amount === undefined) return dash
  if (amount === 0) return '$0.00'
  if (Math.abs(amount) >= 1) {
    return `$${new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(amount)}`
  }
  const leadingZeros = Math.max(0, -Math.floor(Math.log10(Math.abs(amount))) - 1)
  return `$${amount.toFixed(leadingZeros + 4)}`
}

/** A 0–1 ratio as a percentage with two decimals. */
export function formatPercent(ratio: number | null | undefined): string {
  return ratio === null || ratio === undefined ? dash : `${(ratio * 100).toFixed(2)} %`
}

/** Identifiers are truncated in the middle; the full value goes in a tooltip and a copy button (design-system.md section 3.2). */
export function truncateMiddle(value: string, head = 8, tail = 4): string {
  return value.length <= head + tail + 1 ? value : `${value.slice(0, head)}…${value.slice(-tail)}`
}

/** "2 min ago". */
export function formatRelative(iso: string, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return `${seconds} s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`
  return `${Math.floor(seconds / 86400)} d ago`
}

/** An absolute UTC timestamp, to the second: 2026-10-15 12:00:05 UTC. */
export function formatTimestamp(iso: string | null | undefined): string {
  return iso ? `${new Date(iso).toISOString().slice(0, 19).replace('T', ' ')} UTC` : dash
}
