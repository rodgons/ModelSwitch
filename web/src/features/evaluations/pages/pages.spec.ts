import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMemoryHistory, createRouter } from 'vue-router'

// Screen-level smoke tests: each page renders real API shapes (api.md section 3) end to end, with fetch answered per path.
type Routes = Record<string, unknown>
const requests: { method: string; path: string; body: unknown }[] = []

function stubApi(routes: Routes) {
  const BaseRequest = globalThis.Request
  vi.stubGlobal(
    'Request',
    class extends BaseRequest {
      constructor(input: RequestInfo | URL, init?: RequestInit) {
        super(typeof input === 'string' ? new URL(input, 'http://localhost') : input, init)
      }
    },
  )
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      const url = new URL(request.url)
      const text = request.method === 'GET' ? '' : await request.text()
      requests.push({
        method: request.method,
        path: url.pathname,
        body: text ? JSON.parse(text) : null,
      })
      const key = `${request.method} ${url.pathname}`
      if (!(key in routes)) return new Response('{}', { status: 404 })
      return new Response(JSON.stringify(routes[key]), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
}

async function render(path: string, page: () => Promise<unknown>, routePath: string) {
  const { useSession } = await import('@/app/session')
  const pinia = createPinia()
  setActivePinia(pinia)
  useSession().administrator = {
    id: 'a1',
    email: 'op@example.com',
    role: 'operator',
    allTenants: true,
    tenantIds: [],
  }
  const component = ((await page()) as { default: object }).default
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: routePath, component, props: true },
      { path: '/:rest(.*)*', component: { template: '<div />' } },
    ],
  })
  await router.push(path)
  await router.isReady()
  const wrapper = mount(
    { template: '<RouterView />' },
    {
      global: {
        plugins: [
          pinia,
          router,
          [
            VueQueryPlugin,
            { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) },
          ],
        ],
        stubs: { ScatterChart: { template: '<div data-chart />' } },
      },
    },
  )
  await flushPromises()
  await flushPromises()
  return wrapper
}

const target = (label: string) => ({
  label,
  alias: 'balanced',
  policyId: 'p',
  policyVersion: 3,
  providerModelId: null,
  providerModel: null,
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
  requests.length = 0
})

describe('Comparison page', () => {
  const run = (runId: string, status: string, quality: number | null, judged = false) => ({
    runId,
    status,
    target: target('balanced@v3'),
    prompt: { id: 'pr', version: 2 },
    evaluator: { kind: judged ? 'llm_judge' : 'classification', version: '1' },
    params: { temperature: 0, topP: null, seed: null, maxOutputTokens: null },
    configSha256: 'abc',
    casesTotal: 10,
    casesDone: 10,
    quality: quality === null ? null : { metric: 'accuracy', value: quality },
    erroredRate: 0.1,
    latencyP50Ms: 420,
    latencyP95Ms: 1300,
    candidateCostPerCase: 0.0012,
    candidateCost: 0.012,
    judgeCost: judged ? 0.004 : 0,
    totalCost: 0.016,
    modelJudged: judged,
    aggregatesLive: false,
    error: null,
    variance: { runs: 3, min: 0.8, max: 0.9, mean: 0.85, stdDev: 0.05 },
  })

  it('lines runs up with quality, variance, costs, the model-judged label and per-class metrics', async () => {
    stubApi({
      'GET /admin/comparisons/c1': {
        id: 'c1',
        tenantId: 't1',
        datasetId: 'd1',
        datasetVersion: 2,
        name: 'mini vs local',
        createdAt: '2026-10-16T00:00:00Z',
        currency: 'USD',
        runs: [run('r1', 'succeeded', 0.85), run('r2', 'succeeded', 0.7, true)],
      },
      'GET /admin/datasets/d1': {
        id: 'd1',
        name: 'Intents',
        tenantId: 't1',
        taskKind: 'classification',
        latestVersion: 2,
        createdAt: '',
      },
      'GET /admin/evaluations/r1': {
        id: 'r1',
        status: 'succeeded',
        aggregates: {
          evaluatorKind: 'classification',
          classification: {
            perClass: [{ label: 'spam', precision: 0.9, recall: 0.8, f1: 0.847, support: 5 }],
            macroF1: 0.847,
            confusion: { rows: ['spam'], columns: ['spam', 'unparseable'], counts: [[4, 1]] },
          },
        },
      },
      'GET /admin/evaluations/r2': { id: 'r2', status: 'succeeded', aggregates: null },
    })
    const wrapper = await render(
      '/comparisons/c1',
      () => import('./ComparisonPage.vue'),
      '/comparisons/:id',
    )
    const text = wrapper.text()
    expect(text).toContain('mini vs local')
    expect(text).toContain('1. balanced@v3')
    expect(text).toContain('0.850')
    expect(text).toContain('3 runs: mean 0.850')
    expect(text).toContain('Model-judged')
    expect(text).toContain('Judge cost')
    expect(text).toContain('not deterministic')
    expect(text).toContain('Per-class metrics: 1. balanced@v3')
    expect(text).toContain('Macro-F1')
    expect(text).toContain('unparseable')
    expect(text).toContain('Run again')
    expect(wrapper.find('a[href="/admin/evaluations/r1/export?format=csv"]').exists()).toBe(true)
  })
})

describe('Run builder', () => {
  it('prefills labels from the Dataset Version and estimates once a target and Prompt are chosen', async () => {
    stubApi({
      'GET /admin/tenants': { items: [{ id: 't1', name: 'Acme' }], nextCursor: null },
      'GET /admin/datasets': { items: [], nextCursor: null },
      'GET /admin/datasets/d1': {
        id: 'd1',
        tenantId: 't1',
        name: 'Intents',
        taskKind: 'classification',
        latestVersion: 2,
        createdAt: '',
      },
      'GET /admin/datasets/d1/versions': {
        items: [
          {
            datasetId: 'd1',
            version: 2,
            caseCount: 10,
            contentSha256: 'x',
            scoringSchema: null,
            createdAt: '',
            createdByAdminId: null,
          },
        ],
        nextCursor: null,
      },
      'GET /admin/datasets/d1/versions/2': {
        datasetId: 'd1',
        version: 2,
        caseCount: 10,
        contentSha256: 'x',
        scoringSchema: { taskKind: 'classification', labels: ['ham', 'spam'] },
        createdAt: '',
        createdByAdminId: null,
      },
      'GET /admin/prompts': {
        items: [{ id: 'p1', tenantId: 't1', name: 'Classify', latestVersion: 1, createdAt: '' }],
        nextCursor: null,
      },
      'GET /admin/prompts/p1/versions': {
        items: [{ promptId: 'p1', version: 1 }],
        nextCursor: null,
      },
      'GET /admin/routing-policies': {
        items: [{ id: 'pol', aliases: ['balanced'] }],
        nextCursor: null,
      },
      'GET /admin/provider-health': { items: [] },
      'POST /admin/evaluations/estimate': {
        currency: 'USD',
        total: 0.42,
        configurations: [
          {
            index: 0,
            target: 'balanced@v3',
            cases: 10,
            estimatedCases: 10,
            candidateWorstCase: 0.42,
            judgeWorstCase: 0,
            worstCase: 0.42,
            maxPerCaseWorstCase: 0.05,
            exceedsMaxEstimatedCost: true,
            failures: { template_variable_missing: 2 },
          },
        ],
      },
    })
    const wrapper = await render(
      '/evaluations/new?datasetId=d1&version=2',
      () => import('./RunBuilderPage.vue'),
      '/evaluations/new',
    )
    await flushPromises()
    expect(wrapper.text()).toContain('Configuration 1')
    const labels = wrapper.findAll('textarea').find((t) => t.element.value.includes('spam'))
    expect(labels?.element.value).toBe('ham\nspam')

    const selects = wrapper.findAll('select')
    const alias = selects.find((s) =>
      s.findAll('option').some((o) => o.text() === 'Choose a Model Alias'),
    )!
    await alias.setValue('balanced')
    const prompt = selects.find((s) =>
      s.findAll('option').some((o) => o.text() === 'Choose a Prompt'),
    )!
    await prompt.setValue('p1')
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 700))
    await flushPromises()

    const estimate = requests.find((r) => r.path === '/admin/evaluations/estimate')
    expect(estimate?.body).toMatchObject({
      tenantId: 't1',
      datasetId: 'd1',
      datasetVersion: 2,
      configurations: [
        {
          target: { alias: 'balanced' },
          prompt: { id: 'p1', version: 1 },
          evaluator: { kind: 'classification', config: { labels: ['ham', 'spam'] } },
        },
      ],
    })
    const text = wrapper.text()
    expect(text).toContain('$0.4200')
    expect(text).toContain('Over the cost cap')
    expect(text).toContain('template_variable_missing')
  })
})
