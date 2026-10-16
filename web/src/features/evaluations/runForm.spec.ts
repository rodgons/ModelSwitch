import { describe, expect, it } from 'vitest'
import {
  applySchema,
  collectErrors,
  estimateBody,
  evaluatorKindsFor,
  formConfigJson,
  groupRunErrors,
  newConfiguration,
  type RunForm,
  toRequest,
} from './runForm'

const form = (overrides: Partial<RunForm> = {}): RunForm => ({
  tenantId: 't1',
  datasetId: 'd1',
  datasetVersion: '3',
  name: 'mini vs local',
  concurrency: '',
  maxEstimatedCost: '',
  configurations: [
    {
      ...newConfiguration('classification', { taskKind: 'classification', labels: ['a', 'b'] }),
      alias: 'balanced',
      promptId: 'p1',
      promptVersion: '2',
    },
  ],
  ...overrides,
})

describe('run builder evaluators', () => {
  it('offers the Evaluators that fit the task kind, the natural one first', () => {
    expect(evaluatorKindsFor('extraction')).toEqual(['json_fields', 'exact_match'])
    expect(evaluatorKindsFor('classification')).toEqual(['classification', 'exact_match'])
    expect(evaluatorKindsFor('free_text')).toEqual([
      'llm_judge',
      'reference_similarity',
      'exact_match',
    ])
  })

  it('prefills classification labels and json_fields fields from the scoring schema', () => {
    const c = newConfiguration('classification', {
      taskKind: 'classification',
      labels: ['spam', 'ham'],
    })
    expect(c.evaluatorKind).toBe('classification')
    expect(c.labels).toBe('spam\nham')
    const e = applySchema(newConfiguration('extraction', null), {
      taskKind: 'extraction',
      fields: ['email', 'name'],
    })
    expect(e.fields).toEqual([
      { name: 'email', comparator: '', tolerance: '' },
      { name: 'name', comparator: '', tolerance: '' },
    ])
  })
})

describe('run builder request body', () => {
  it('maps the form to CreateEvaluationRequest, leaving empty optionals out', () => {
    const result = toRequest(form())
    expect(result).toEqual({
      ok: true,
      body: {
        tenantId: 't1',
        datasetId: 'd1',
        datasetVersion: 3,
        name: 'mini vs local',
        concurrency: null,
        maxEstimatedCost: null,
        configurations: [
          {
            target: { alias: 'balanced', providerModelId: null },
            prompt: { id: 'p1', version: 2 },
            params: null,
            evaluator: { kind: 'classification', version: '1', config: { labels: ['a', 'b'] } },
          },
        ],
      },
    })
  })

  it('sends a pinned Provider Model target, params and run limits as numbers', () => {
    const base = form()
    const result = toRequest({
      ...base,
      concurrency: '8',
      maxEstimatedCost: '2.50',
      configurations: [
        {
          ...base.configurations[0]!,
          targetKind: 'providerModel',
          providerModelId: 'm1',
          temperature: '0',
          seed: '7',
          maxOutputTokens: '256',
        },
      ],
    })
    if (!result.ok) throw new Error('expected ok')
    expect(result.body.concurrency).toBe(8)
    expect(result.body.maxEstimatedCost).toBe(2.5)
    expect(result.body.configurations![0]).toMatchObject({
      target: { alias: null, providerModelId: 'm1' },
      params: { temperature: 0, topP: null, seed: 7, maxOutputTokens: 256 },
    })
  })

  it('builds each kind-specific config, omitting blank fields', () => {
    const c = newConfiguration('extraction', { taskKind: 'extraction', fields: ['email', 'age'] })
    c.fields[1] = { name: 'age', comparator: 'number', tolerance: '1' }
    c.passThreshold = '0.5'
    const body = toRequest(
      form({ configurations: [{ ...c, alias: 'x', promptId: 'p', promptVersion: '1' }] }),
    )
    if (!body.ok) throw new Error('expected ok')
    expect(body.body.configurations![0]!.evaluator!.config).toEqual({
      defaultComparator: 'exact',
      fields: { age: { comparator: 'number', tolerance: 1 } },
      passThreshold: 0.5,
    })

    const judge = {
      ...newConfiguration('free_text', null),
      judgeAlias: 'judge',
      rubric: 'Be fair.',
    }
    const judged = toRequest(
      form({ configurations: [{ ...judge, alias: 'x', promptId: 'p', promptVersion: '1' }] }),
    )
    if (!judged.ok) throw new Error('expected ok')
    expect(judged.body.configurations![0]!.evaluator).toEqual({
      kind: 'llm_judge',
      version: '1',
      config: { judgeAlias: 'judge', rubric: 'Be fair.' },
    })
  })

  it('opens the JSON view on what the form describes', () => {
    expect(formConfigJson(form().configurations[0]!)).toBe(
      '{\n  "labels": [\n    "a",\n    "b"\n  ]\n}',
    )
    expect(formConfigJson(newConfiguration('free_text', null))).toContain('"judgeAlias": ""')
    expect(
      formConfigJson({ ...newConfiguration('extraction', null), evaluatorKind: 'exact_match' }),
    ).toBe('{}')
  })

  it('takes the config from the JSON view when chosen', () => {
    const c = {
      ...form().configurations[0]!,
      configMode: 'json' as const,
      configJson: '{"labels":["z"]}',
    }
    const body = toRequest(form({ configurations: [c] }))
    if (!body.ok) throw new Error('expected ok')
    expect(body.body.configurations![0]!.evaluator!.config).toEqual({ labels: ['z'] })
  })

  it('reports malformed numbers and JSON under the server keys, before sending', () => {
    const c = {
      ...form().configurations[0]!,
      temperature: 'hot',
      configMode: 'json' as const,
      configJson: '{nope',
    }
    expect(toRequest(form({ concurrency: 'x', configurations: [c] }))).toEqual({
      ok: false,
      errors: {
        concurrency: ['Must be a whole number.'],
        'configurations[0].params.temperature': ['Must be a number.'],
        'configurations[0].evaluator.config': ['Not valid JSON.'],
      },
    })
  })
})

describe('run builder estimate', () => {
  it('estimates only once every reference is chosen', () => {
    expect(estimateBody(form())).not.toBeNull()
    expect(estimateBody(form({ datasetVersion: '' }))).toBeNull()
    const noPrompt = { ...form().configurations[0]!, promptVersion: '' }
    expect(estimateBody(form({ configurations: [noPrompt] }))).toBeNull()
    const bad = { ...form().configurations[0]!, seed: '1.5' }
    expect(estimateBody(form({ configurations: [bad] }))).toBeNull()
  })
})

describe('run builder validation errors', () => {
  it('splits keys into run-level fields and per-configuration fields', () => {
    const grouped = groupRunErrors({
      name: ['Required.'],
      configurations: ['At most 6.'],
      'configurations[1].target.alias': ['No such Model Alias.'],
      'configurations[1].evaluator.config.labels[1]': ['Labels must be distinct.'],
      'configurations[0]': ['Is required.'],
    })
    expect(grouped.top).toEqual({ name: ['Required.'], configurations: ['At most 6.'] })
    expect(grouped.configuration(1)).toEqual({
      'target.alias': ['No such Model Alias.'],
      'evaluator.config.labels[1]': ['Labels must be distinct.'],
    })
    expect(grouped.configuration(0)).toEqual({ '': ['Is required.'] })
    expect(grouped.configuration(4)).toEqual({})
  })

  it('collects a key and everything under it, naming the sub-field', () => {
    const errors = {
      'evaluator.config': ['Invalid.'],
      'evaluator.config.labels[1]': ['Labels must be distinct.'],
      'evaluator.kind': ['Does not fit.'],
    }
    expect(collectErrors(errors, 'evaluator.config')).toEqual([
      'Invalid.',
      'labels[1]: Labels must be distinct.',
    ])
    expect(collectErrors(errors, 'target')).toEqual([])
  })
})
