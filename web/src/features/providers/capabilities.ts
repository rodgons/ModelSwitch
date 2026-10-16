/** The capability editor's form state (data-model.md, provider_model.capabilities). Numbers are kept as text while editing. */
export interface CapabilityForm {
  streaming: boolean
  jsonObject: boolean
  jsonSchema: boolean
  contextWindow: string
  maxOutputTokens: string
  parameters: string
  tokenizer: string
}

export const knownParameters = [
  'temperature',
  'top_p',
  'seed',
  'stop',
  'presence_penalty',
  'frequency_penalty',
]

export function toForm(capabilities: unknown): CapabilityForm {
  const c = (capabilities ?? {}) as Record<string, unknown>
  return {
    streaming: c.streaming === true,
    jsonObject: c.jsonObject === true,
    jsonSchema: c.jsonSchema === true,
    contextWindow: typeof c.contextWindow === 'number' ? String(c.contextWindow) : '',
    maxOutputTokens: typeof c.maxOutputTokens === 'number' ? String(c.maxOutputTokens) : '',
    parameters: Array.isArray(c.parameters) ? c.parameters.join(', ') : '',
    tokenizer: typeof c.tokenizer === 'string' ? c.tokenizer : '',
  }
}

/** The capabilities document the API validates key by key. Empty fields are left out rather than sent as zero. */
export function fromForm(form: CapabilityForm): Record<string, unknown> {
  const capabilities: Record<string, unknown> = {
    streaming: form.streaming,
    jsonObject: form.jsonObject,
    jsonSchema: form.jsonSchema,
  }
  if (form.contextWindow.trim()) capabilities.contextWindow = Number(form.contextWindow)
  if (form.maxOutputTokens.trim()) capabilities.maxOutputTokens = Number(form.maxOutputTokens)
  const parameters = form.parameters
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean)
  if (parameters.length) capabilities.parameters = parameters
  if (form.tokenizer) capabilities.tokenizer = form.tokenizer
  return capabilities
}

/** "Streaming · JSON schema · 128,000 ctx" for the models table. */
export function summarize(capabilities: unknown): string {
  const f = toForm(capabilities)
  const parts = [
    f.streaming && 'Streaming',
    f.jsonObject && 'JSON object',
    f.jsonSchema && 'JSON schema',
    f.contextWindow && `${Number(f.contextWindow).toLocaleString('en-US')} ctx`,
  ]
  return parts.filter(Boolean).join(' · ') || 'No Capabilities declared'
}
