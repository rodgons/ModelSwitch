/** One message of a Prompt Version template, as the editor holds it. */
export interface TemplateMessage {
  role: string
  content: string
}

export const roles = ['system', 'user', 'assistant'] as const

// The server's placeholder (PromptTemplate.Placeholder): an identifier between double braces, spaces allowed inside.
const placeholder = /\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}/g

/** The template's `{{variable}}` names, each once, in order of first use: the keys every Case `input` must have. */
export function detectVariables(messages: readonly TemplateMessage[]): string[] {
  const names = new Set<string>()
  for (const message of messages)
    for (const match of message.content.matchAll(placeholder)) names.add(match[1]!)
  return [...names]
}

export type ParsedResponseFormat =
  { ok: true; value: Record<string, unknown> | null } | { ok: false; error: string }

/** The optional response format field: blank means none; otherwise it must be a JSON object (the server checks its fields). */
export function parseResponseFormat(text: string): ParsedResponseFormat {
  if (!text.trim()) return { ok: true, value: null }
  let value: unknown
  try {
    value = JSON.parse(text)
  } catch {
    return { ok: false, error: 'Not valid JSON.' }
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    return { ok: false, error: 'Must be a JSON object, such as {"type": "json_object"}.' }
  return { ok: true, value: value as Record<string, unknown> }
}

/** A copy of the list with the item at `index` moved by `delta` places; unchanged when that would leave the list. */
export function moveItem<T>(list: readonly T[], index: number, delta: number): T[] {
  const target = index + delta
  const copy = [...list]
  if (index < 0 || index >= list.length || target < 0 || target >= list.length) return copy
  const [item] = copy.splice(index, 1)
  copy.splice(target, 0, item as T)
  return copy
}

export interface TemplateErrors {
  /** About the message list or the body as a whole (`messages`, or keys the editor does not place). */
  general: string[]
  /** `responseFormat` and `responseFormat.<field>`, the field named before its message. */
  responseFormat: string[]
  /** The errors of the message at a 0-based index. */
  message(index: number): { role: string[]; content: string[] }
}

const messageKey = /^messages\[(\d+)\]\.(role|content)$/

/** Places a rejected template's `errors` (api.md section 3, Prompt template validation) on the editor's fields. */
export function readTemplateErrors(errors: Record<string, string[]>): TemplateErrors {
  const general: string[] = []
  const responseFormat: string[] = []
  const messages = new Map<number, { role: string[]; content: string[] }>()
  for (const [key, list] of Object.entries(errors)) {
    const match = messageKey.exec(key)
    if (match) {
      const index = Number(match[1])
      const entry = messages.get(index) ?? { role: [], content: [] }
      entry[match[2] as 'role' | 'content'].push(...list)
      messages.set(index, entry)
    } else if (key === 'responseFormat') responseFormat.push(...list)
    else if (key.startsWith('responseFormat.'))
      responseFormat.push(...list.map((m) => `${key.slice('responseFormat.'.length)}: ${m}`))
    else if (key === 'messages') general.push(...list)
    else general.push(...list.map((m) => `${key}: ${m}`))
  }
  return {
    general,
    responseFormat,
    message: (index) => messages.get(index) ?? { role: [], content: [] },
  }
}
