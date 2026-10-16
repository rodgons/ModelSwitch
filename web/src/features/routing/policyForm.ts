/**
 * The policy editor's form view (frontend.md section 2): the fields most edits touch. Anything else in the definition (rules, default,
 * allowedProviders, ...) is kept as it is and edited in the JSON view.
 */
export interface PolicyForm {
  strategy: string
  candidates: string
  deadlineMs: string
  attemptTimeoutMs: string
  maxAttempts: string
  maxRequestCost: string
}

type Definition = Record<string, unknown> & { execution?: Record<string, unknown> }

const text = (value: unknown) => (value === null || value === undefined ? '' : String(value))

export function readForm(definition: unknown): PolicyForm {
  const d = (definition ?? {}) as Definition
  return {
    strategy: text(d.strategy) || 'static',
    candidates: Array.isArray(d.candidates) ? d.candidates.join('\n') : '',
    deadlineMs: text(d.execution?.deadlineMs),
    attemptTimeoutMs: text(d.execution?.attemptTimeoutMs),
    maxAttempts: text(d.execution?.maxAttempts),
    maxRequestCost: text(d.maxRequestCost),
  }
}

/** Writes the form's fields into the definition, leaving every other key untouched; an empty field removes its key. */
export function writeForm(definition: unknown, form: PolicyForm): Definition {
  const d: Definition = { ...((definition ?? {}) as Definition) }
  d.strategy = form.strategy
  d.candidates = form.candidates
    .split('\n')
    .map((c) => c.trim())
    .filter(Boolean)
  const execution: Record<string, unknown> = { ...(d.execution ?? {}) }
  for (const key of ['deadlineMs', 'attemptTimeoutMs', 'maxAttempts'] as const) {
    if (form[key].trim()) execution[key] = Number(form[key])
    else delete execution[key]
  }
  if (Object.keys(execution).length) d.execution = execution
  else delete d.execution
  if (form.maxRequestCost.trim()) d.maxRequestCost = form.maxRequestCost.trim()
  else delete d.maxRequestCost
  return d
}
