import { keepPreviousData, useQueries, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import { pollInterval } from './comparison'
import type { CreateBody } from './runForm'

export interface ComparisonFilters {
  tenantId: string
  datasetId: string
  status: string
}

export const runKeys = {
  all: ['evaluations'] as const,
  comparisons: (filters: ComparisonFilters, cursor: string | null) =>
    ['evaluations', 'comparisons', filters, cursor] as const,
  comparison: (id: string) => ['evaluations', 'comparison', id] as const,
  run: (id: string) => ['evaluations', 'run', id] as const,
  results: (id: string, status: string, cursor: string | null) =>
    ['evaluations', 'results', id, status, cursor] as const,
  allResults: (id: string) => ['evaluations', 'all-results', id] as const,
}

const defined = (entries: Record<string, string | null>) =>
  Object.fromEntries(Object.entries(entries).filter(([, v]) => v)) as Record<string, string>

/** Comparisons newest first, each with its runs; refreshed every two seconds while any run on the page is queued or running. */
export function useComparisons(
  filters: MaybeRefOrGetter<ComparisonFilters>,
  cursor: MaybeRefOrGetter<string | null>,
) {
  return useQuery({
    queryKey: computed(() => runKeys.comparisons({ ...toValue(filters) }, toValue(cursor))),
    queryFn: async () =>
      data(
        await api.GET('/admin/evaluations', {
          params: {
            query: { limit: '20', ...defined({ ...toValue(filters), cursor: toValue(cursor) }) },
          },
        }),
      ),
    placeholderData: keepPreviousData,
    refetchInterval: (query) =>
      pollInterval(query.state.data?.items.flatMap((c) => c.runs.map((r) => r.status)) ?? []),
  })
}

export function useComparison(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => runKeys.comparison(toValue(id))),
    queryFn: async () =>
      data(
        await api.GET('/admin/comparisons/{comparisonId}', {
          params: { path: { comparisonId: toValue(id) } },
        }),
      ),
    refetchInterval: (query) => pollInterval(query.state.data?.runs.map((r) => r.status) ?? []),
  })
}

async function fetchRun(runId: string) {
  return data(await api.GET('/admin/evaluations/{runId}', { params: { path: { runId } } }))
}

export function useRun(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => runKeys.run(toValue(id))),
    queryFn: () => fetchRun(toValue(id)),
    refetchInterval: (query) => pollInterval(query.state.data ? [query.state.data.status] : []),
  })
}

/** Each run's detail (aggregates for per-class and per-field metrics), polled while that run is unfinished. */
export function useRuns(ids: MaybeRefOrGetter<string[]>) {
  return useQueries({
    queries: computed(() =>
      toValue(ids).map((id) => ({
        queryKey: runKeys.run(id),
        queryFn: () => fetchRun(id),
        refetchInterval: (query: { state: { data?: Schemas['RunDetailView'] } }) =>
          pollInterval(query.state.data ? [query.state.data.status] : []),
      })),
    ),
  })
}

export function useResults(
  id: MaybeRefOrGetter<string>,
  status: MaybeRefOrGetter<string>,
  cursor: MaybeRefOrGetter<string | null>,
) {
  return useQuery({
    queryKey: computed(() => runKeys.results(toValue(id), toValue(status), toValue(cursor))),
    queryFn: async () =>
      data(
        await api.GET('/admin/evaluations/{runId}/results', {
          params: {
            path: { runId: toValue(id) },
            query: {
              limit: '50',
              ...defined({ status: toValue(status), cursor: toValue(cursor) }),
            },
          },
        }),
      ),
    placeholderData: keepPreviousData,
  })
}

/**
 * Every Case Result of a run, page after page (at most 5,000 Cases, so at most 25 pages of 200). The results endpoint has no Case
 * filter, so the drill-down loads them once, on demand, and lines Cases up by id across runs.
 */
async function fetchAllResults(runId: string) {
  const items: Schemas['CaseResultView'][] = []
  let cursor: string | null = null
  do {
    const page: Schemas['PagedResultOfCaseResultView'] = data(
      await api.GET('/admin/evaluations/{runId}/results', {
        params: { path: { runId }, query: { limit: '200', ...defined({ cursor }) } },
      }),
    )
    items.push(...page.items)
    cursor = page.nextCursor
  } while (cursor)
  return items
}

export function useAllResults(ids: MaybeRefOrGetter<string[]>, enabled: MaybeRefOrGetter<boolean>) {
  return useQueries({
    queries: computed(() =>
      toValue(ids).map((id) => ({
        queryKey: runKeys.allResults(id),
        queryFn: () => fetchAllResults(id),
        enabled: toValue(enabled),
        staleTime: 30_000,
      })),
    ),
  })
}

export async function estimateRun(body: CreateBody) {
  return data(await api.POST('/admin/evaluations/estimate', { body }))
}

export async function createRun(body: CreateBody) {
  return data(await api.POST('/admin/evaluations', { body }))
}

export async function cancelRun(runId: string) {
  return data(await api.POST('/admin/evaluations/{runId}/cancel', { params: { path: { runId } } }))
}

export async function rerun(runId: string) {
  return data(await api.POST('/admin/evaluations/{runId}/rerun', { params: { path: { runId } } }))
}

export async function recomputeAggregates(runId: string) {
  return data(
    await api.POST('/admin/evaluations/{runId}/aggregates/recompute', {
      params: { path: { runId } },
    }),
  )
}

/** A same-origin download link: the session cookie authenticates a GET, and the server names the file. */
export const exportUrl = (runId: string, format: 'csv' | 'jsonl') =>
  `/admin/evaluations/${encodeURIComponent(runId)}/export?format=${format}`
