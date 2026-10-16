/** The server's upload limit (api.md section 3, Evaluation): larger bodies get 413 payload_too_large, so they are stopped here first. */
export const maxUploadBytes = 10 * 1024 * 1024

const extensions = ['.jsonl', '.ndjson']

/** Why this file cannot be uploaded as a Dataset Version, or null when it can be sent. */
export function checkUploadFile(file: { name: string; size: number }): string | null {
  const name = file.name.toLowerCase()
  if (!extensions.some((extension) => name.endsWith(extension)))
    return 'Choose a .jsonl file: one Case per line.'
  if (file.size === 0) return 'The file is empty. Add at least one Case.'
  if (file.size > maxUploadBytes) return 'The file is larger than 10 MiB, the upload limit.'
  return null
}

export interface LineProblem {
  /** The Case field the message is about (`id`, `expected`, `input.text`…), or null for the line as a whole. */
  field: string | null
  messages: string[]
}

export interface UploadErrors {
  /** Problems with the upload as a whole (`body`, or a key the dashboard does not recognize). */
  general: string[]
  /** Problems per line of the file, by 1-based line number, in line order. */
  lines: { line: number; problems: LineProblem[] }[]
}

const lineKey = /^lines\[(\d+)\](?:\.(.+))?$/

/** Reads the `errors` extension of a rejected upload: keys are `lines[N]`, `lines[N].<field>` (N 1-based) or `body`. */
export function readUploadErrors(errors: Record<string, string[]>): UploadErrors {
  const general: string[] = []
  const byLine = new Map<number, LineProblem[]>()
  for (const [key, messages] of Object.entries(errors)) {
    const match = lineKey.exec(key)
    if (!match) {
      general.push(...(key === 'body' ? messages : messages.map((m) => `${key}: ${m}`)))
      continue
    }
    const line = Number(match[1])
    const problems = byLine.get(line) ?? []
    problems.push({ field: match[2] ?? null, messages })
    byLine.set(line, problems)
  }
  const lines = [...byLine.entries()]
    .sort(([a], [b]) => a - b)
    .map(([line, problems]) => ({
      line,
      problems: problems.sort((a, b) =>
        a.field === null ? -1 : b.field === null ? 1 : a.field.localeCompare(b.field),
      ),
    }))
  return { general, lines }
}
