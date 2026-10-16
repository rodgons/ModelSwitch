import type { Schemas } from '@/api/client'

/**
 * The run builder's form (frontend.md section 2, `/evaluations/new`) and its mapping to `CreateEvaluationRequest` (api.md section 3,
 * Evaluation Runs and Comparisons). Inputs are kept as typed text; numbers are parsed here, and anything malformed is reported under
 * the key the server would use, so one error display serves both.
 */

export const comparators = ['exact', 'casefold', 'number', 'set'] as const

export interface FieldRule {
  name: string
  /** Empty: the default comparator. */
  comparator: string
  tolerance: string
}

export interface ConfigForm {
  targetKind: 'alias' | 'providerModel'
  alias: string
  providerModelId: string
  promptId: string
  promptVersion: string
  temperature: string
  topP: string
  seed: string
  maxOutputTokens: string
  evaluatorKind: string
  configMode: 'form' | 'json'
  caseFold: boolean
  labels: string
  defaultComparator: string
  fields: FieldRule[]
  passThreshold: string
  judgeAlias: string
  rubric: string
  configJson: string
}

export interface RunForm {
  tenantId: string
  datasetId: string
  datasetVersion: string
  name: string
  concurrency: string
  maxEstimatedCost: string
  configurations: ConfigForm[]
}

export type CreateBody = Schemas['CreateEvaluationRequest']
export type ScoringSchema = { taskKind?: string; labels?: string[]; fields?: string[] } | null

export const maxConfigurations = 6

const kindsByTask: Record<string, string[]> = {
  extraction: ['json_fields', 'exact_match'],
  classification: ['classification', 'exact_match'],
  free_text: ['llm_judge', 'reference_similarity', 'exact_match'],
}

/** The Evaluator kinds that fit a Dataset's task kind (evaluation.md section 3), the natural one first. */
export function evaluatorKindsFor(taskKind: string): string[] {
  return kindsByTask[taskKind] ?? ['exact_match']
}

/** Fills the parts of a configuration that come from the Dataset Version: classification labels and json_fields fields. */
export function applySchema(config: ConfigForm, schema: ScoringSchema): ConfigForm {
  return {
    ...config,
    labels: (schema?.labels ?? []).join('\n'),
    fields: (schema?.fields ?? []).map((name) => ({ name, comparator: '', tolerance: '' })),
  }
}

export function newConfiguration(taskKind: string, schema: ScoringSchema): ConfigForm {
  return applySchema(
    {
      targetKind: 'alias',
      alias: '',
      providerModelId: '',
      promptId: '',
      promptVersion: '',
      temperature: '',
      topP: '',
      seed: '',
      maxOutputTokens: '',
      evaluatorKind: evaluatorKindsFor(taskKind)[0]!,
      configMode: 'form',
      caseFold: false,
      labels: '',
      defaultComparator: 'exact',
      fields: [],
      passThreshold: '',
      judgeAlias: '',
      rubric: '',
      configJson: '',
    },
    schema,
  )
}

type Errors = Record<string, string[]>

const decimal = /^-?\d+(\.\d+)?$/
const whole = /^-?\d+$/

function number(text: string, key: string, errors: Errors, integer = false): number | null {
  const value = text.trim()
  if (!value) return null
  if (!(integer ? whole : decimal).test(value)) {
    errors[key] = [integer ? 'Must be a whole number.' : 'Must be a number.']
    return null
  }
  return Number(value)
}

/** The Evaluator config the form describes (null when the kind needs none and none is set). */
function evaluatorConfig(
  c: ConfigForm,
  key: string,
  errors: Errors,
): Record<string, unknown> | null {
  if (c.configMode === 'json') {
    if (!c.configJson.trim()) return null
    try {
      const value: unknown = JSON.parse(c.configJson)
      if (typeof value === 'object' && value !== null && !Array.isArray(value))
        return value as Record<string, unknown>
      errors[key] = ['Must be a JSON object.']
    } catch {
      errors[key] = ['Not valid JSON.']
    }
    return null
  }
  const config: Record<string, unknown> = {}
  const threshold = number(c.passThreshold, `${key}.passThreshold`, errors)
  switch (c.evaluatorKind) {
    case 'exact_match':
      if (c.caseFold) config.caseFold = true
      break
    case 'classification':
      config.labels = c.labels
        .split('\n')
        .map((l) => l.trim())
        .filter(Boolean)
      break
    case 'json_fields': {
      config.defaultComparator = c.defaultComparator || 'exact'
      const fields: Record<string, unknown> = {}
      for (const field of c.fields) {
        if (!field.comparator) continue
        const rule: Record<string, unknown> = { comparator: field.comparator }
        const tolerance = number(field.tolerance, `${key}.fields.${field.name}.tolerance`, errors)
        if (field.comparator === 'number' && tolerance !== null) rule.tolerance = tolerance
        fields[field.name] = rule
      }
      if (Object.keys(fields).length) config.fields = fields
      if (threshold !== null) config.passThreshold = threshold
      break
    }
    case 'llm_judge':
      config.judgeAlias = c.judgeAlias.trim()
      config.rubric = c.rubric
      if (threshold !== null) config.passThreshold = threshold
      break
    case 'reference_similarity':
      if (threshold !== null) config.passThreshold = threshold
      break
  }
  return Object.keys(config).length ? config : null
}

/** The form's config as JSON text, to start the JSON view from (malformed numbers are left out). */
export function formConfigJson(c: ConfigForm): string {
  return JSON.stringify(evaluatorConfig({ ...c, configMode: 'form' }, 'config', {}) ?? {}, null, 2)
}

export type ToRequest = { ok: true; body: CreateBody } | { ok: false; errors: Errors }

export function toRequest(form: RunForm): ToRequest {
  const errors: Errors = {}
  const configurations = form.configurations.map((c, i) => {
    const key = `configurations[${i}]`
    const params = {
      temperature: number(c.temperature, `${key}.params.temperature`, errors),
      topP: number(c.topP, `${key}.params.topP`, errors),
      seed: number(c.seed, `${key}.params.seed`, errors, true),
      maxOutputTokens: number(c.maxOutputTokens, `${key}.params.maxOutputTokens`, errors, true),
    }
    const alias = c.targetKind === 'alias'
    return {
      target: {
        alias: alias ? c.alias.trim() || null : null,
        providerModelId: alias ? null : c.providerModelId || null,
      },
      prompt: {
        id: c.promptId || null,
        version: number(c.promptVersion, `${key}.prompt.version`, errors, true),
      },
      params: Object.values(params).some((v) => v !== null) ? params : null,
      evaluator: {
        kind: c.evaluatorKind,
        version: '1',
        config: evaluatorConfig(c, `${key}.evaluator.config`, errors) as never,
      },
    }
  })
  const body: CreateBody = {
    tenantId: form.tenantId || null,
    datasetId: form.datasetId || null,
    datasetVersion: number(form.datasetVersion, 'datasetVersion', errors, true),
    name: form.name,
    concurrency: number(form.concurrency, 'concurrency', errors, true),
    maxEstimatedCost: number(form.maxEstimatedCost, 'maxEstimatedCost', errors),
    configurations,
  }
  return Object.keys(errors).length ? { ok: false, errors } : { ok: true, body }
}

/** The body to estimate, or null until every reference is chosen and every number parses. */
export function estimateBody(form: RunForm): CreateBody | null {
  const complete =
    form.tenantId &&
    form.datasetId &&
    form.datasetVersion &&
    form.configurations.length > 0 &&
    form.configurations.every(
      (c) =>
        (c.targetKind === 'alias' ? c.alias.trim() : c.providerModelId) &&
        c.promptId &&
        c.promptVersion &&
        c.evaluatorKind,
    )
  if (!complete) return null
  const result = toRequest(form)
  return result.ok ? result.body : null
}

const configKey = /^configurations\[(\d+)\](?:\.(.+))?$/

export interface GroupedRunErrors {
  /** Run-level keys (`name`, `datasetVersion`, `configurations`…). */
  top: Errors
  /** The keys under `configurations[i].`, without that prefix; `''` for the configuration as a whole. */
  configuration(index: number): Errors
}

export function groupRunErrors(errors: Errors): GroupedRunErrors {
  const top: Errors = {}
  const configs = new Map<number, Errors>()
  for (const [key, messages] of Object.entries(errors)) {
    const match = configKey.exec(key)
    if (!match) {
      top[key] = messages
      continue
    }
    const index = Number(match[1])
    const entry = configs.get(index) ?? {}
    entry[match[2] ?? ''] = messages
    configs.set(index, entry)
  }
  return { top, configuration: (index) => configs.get(index) ?? {} }
}

/** The messages of `prefix` and of every key under it (`prefix.x`, `prefix[0]`), each sub-key named before its message. */
export function collectErrors(errors: Errors, prefix: string): string[] {
  const out: string[] = []
  for (const [key, messages] of Object.entries(errors)) {
    if (key === prefix) out.push(...messages)
    else if (key.startsWith(`${prefix}.`) || key.startsWith(`${prefix}[`)) {
      const rest = key.slice(prefix.length).replace(/^\./, '')
      out.push(...messages.map((m) => `${rest}: ${m}`))
    }
  }
  return out
}
