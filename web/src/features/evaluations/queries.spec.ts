import { afterEach, describe, expect, it, vi } from 'vitest'

describe('dataset upload request', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.resetModules()
  })

  async function load(response: Response) {
    const fetch = vi.fn(async (request: Request) => {
      void request
      return response
    })
    vi.stubGlobal('fetch', fetch)
    // The client calls same-origin paths; outside a browser, Request needs an origin to resolve them against.
    const BaseRequest = globalThis.Request
    vi.stubGlobal(
      'Request',
      class extends BaseRequest {
        constructor(input: RequestInfo | URL, init?: RequestInit) {
          super(typeof input === 'string' ? new URL(input, 'http://localhost') : input, init)
        }
      },
    )
    const client = await import('@/api/client')
    const queries = await import('./queries')
    return { fetch, client, queries }
  }

  it('sends the file as the raw body with the JSONL type and the antiforgery token', async () => {
    const created = { datasetId: 'd1', version: 2, caseCount: 1 }
    const { fetch, client, queries } = await load(
      new Response(JSON.stringify(created), {
        status: 201,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    client.setAntiforgeryToken('xsrf')
    const file = new Blob(['{"id":"c-1","input":{},"expected":"a"}\n'])

    await expect(queries.uploadDatasetVersion('d1', file)).resolves.toMatchObject({ version: 2 })

    const request = fetch.mock.calls[0]![0]
    expect(request.method).toBe('POST')
    expect(new URL(request.url, 'http://x').pathname).toBe('/admin/datasets/d1/versions')
    expect(request.headers.get('Content-Type')).toBe('application/x-ndjson')
    expect(request.headers.get('X-XSRF-TOKEN')).toBe('xsrf')
    expect(await request.text()).toBe('{"id":"c-1","input":{},"expected":"a"}\n')
  })

  it('rejects with the line-numbered errors of a 400', async () => {
    const { queries } = await load(
      new Response(
        JSON.stringify({ errorCode: 'validation_failed', errors: { 'lines[2].id': ['Missing.'] } }),
        { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    )
    await expect(queries.uploadDatasetVersion('d1', new Blob(['x']))).rejects.toMatchObject({
      errorCode: 'validation_failed',
      fieldErrors: { 'lines[2].id': ['Missing.'] },
    })
  })
})

describe('evaluation delete requests', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    vi.resetModules()
  })

  async function load(response: () => Response) {
    const fetch = vi.fn(async (request: Request) => {
      void request
      return response()
    })
    vi.stubGlobal('fetch', fetch)
    const BaseRequest = globalThis.Request
    vi.stubGlobal(
      'Request',
      class extends BaseRequest {
        constructor(input: RequestInfo | URL, init?: RequestInit) {
          super(typeof input === 'string' ? new URL(input, 'http://localhost') : input, init)
        }
      },
    )
    const client = await import('@/api/client')
    client.setAntiforgeryToken('xsrf')
    const queries = await import('./queries')
    const runQueries = await import('./runQueries')
    return { fetch, queries, runQueries }
  }

  it.each([
    ['dataset', '/admin/datasets/d1'],
    ['prompt', '/admin/prompts/p1'],
    ['comparison', '/admin/comparisons/c1'],
  ])('deletes a %s with DELETE and the antiforgery token', async (kind, path) => {
    const { fetch, queries, runQueries } = await load(() => new Response(null, { status: 204 }))

    const deletes: Record<string, () => Promise<void>> = {
      dataset: () => queries.deleteDataset('d1'),
      prompt: () => queries.deletePrompt('p1'),
      comparison: () => runQueries.deleteComparison('c1'),
    }
    await expect(deletes[kind]!()).resolves.toBeUndefined()

    const request = fetch.mock.calls[0]![0]
    expect(request.method).toBe('DELETE')
    expect(new URL(request.url, 'http://x').pathname).toBe(path)
    expect(request.headers.get('X-XSRF-TOKEN')).toBe('xsrf')
  })

  it('rejects with the conflict when the dataset is still in use', async () => {
    const { queries } = await load(
      () =>
        new Response(
          JSON.stringify({
            errorCode: 'conflict',
            detail: 'The dataset is used by 2 comparison(s); delete them first.',
          }),
          { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
        ),
    )

    await expect(queries.deleteDataset('d1')).rejects.toMatchObject({
      status: 409,
      errorCode: 'conflict',
    })
  })
})
