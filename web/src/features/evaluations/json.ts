/** A Case value on one line for a table cell, cut to `max` characters with an ellipsis; the full value is shown on expand. */
export function compactJson(value: unknown, max = 80): { text: string; truncated: boolean } {
  if (value === null || value === undefined) return { text: '—', truncated: false }
  const text = JSON.stringify(value)
  return text.length <= max
    ? { text, truncated: false }
    : { text: `${text.slice(0, max - 1)}…`, truncated: true }
}

export function prettyJson(value: unknown): string {
  return JSON.stringify(value ?? null, null, 2)
}
