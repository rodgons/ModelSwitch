import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

export const evaluationKeys = {
  datasets: ['datasets'] as const,
  datasetList: (tenantId: string, cursor: string | null) =>
    ['datasets', 'list', tenantId, cursor] as const,
  dataset: (id: string) => ['datasets', 'detail', id] as const,
  datasetVersions: (id: string) => ['datasets', 'versions', id] as const,
  datasetVersion: (id: string, version: number) => ['datasets', 'version', id, version] as const,
  cases: (id: string, version: number, cursor: string | null) =>
    ['datasets', 'cases', id, version, cursor] as const,
  prompts: ['prompts'] as const,
  promptList: (tenantId: string, cursor: string | null) =>
    ['prompts', 'list', tenantId, cursor] as const,
  prompt: (id: string) => ['prompts', 'detail', id] as const,
  promptVersions: (id: string) => ['prompts', 'versions', id] as const,
}

/** The list query string: an empty Tenant means every Tenant in the Administrator's grant. */
function listQuery(tenantId: string, cursor: string | null, limit = '50') {
  return {
    limit,
    ...(tenantId ? { tenantId } : {}),
    ...(cursor ? { cursor } : {}),
  }
}

export function useDatasets(
  tenantId: MaybeRefOrGetter<string>,
  cursor: MaybeRefOrGetter<string | null>,
) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.datasetList(toValue(tenantId), toValue(cursor))),
    queryFn: async () =>
      data(
        await api.GET('/admin/datasets', {
          params: { query: listQuery(toValue(tenantId), toValue(cursor)) },
        }),
      ),
    placeholderData: keepPreviousData,
  })
}

export function useDataset(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.dataset(toValue(id))),
    enabled: computed(() => toValue(id) !== ''),
    queryFn: async () =>
      data(
        await api.GET('/admin/datasets/{datasetId}', {
          params: { path: { datasetId: toValue(id) } },
        }),
      ),
  })
}

export function useDatasetVersions(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.datasetVersions(toValue(id))),
    enabled: computed(() => toValue(id) !== ''),
    queryFn: async () =>
      data(
        await api.GET('/admin/datasets/{datasetId}/versions', {
          params: { path: { datasetId: toValue(id) }, query: { limit: '200' } },
        }),
      ),
  })
}

export function useDatasetVersion(id: MaybeRefOrGetter<string>, version: MaybeRefOrGetter<number>) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.datasetVersion(toValue(id), toValue(version))),
    enabled: computed(() => toValue(id) !== '' && toValue(version) > 0),
    queryFn: async () =>
      data(
        await api.GET('/admin/datasets/{datasetId}/versions/{version}', {
          params: { path: { datasetId: toValue(id), version: toValue(version) } },
        }),
      ),
  })
}

export function useCases(
  id: MaybeRefOrGetter<string>,
  version: MaybeRefOrGetter<number>,
  cursor: MaybeRefOrGetter<string | null>,
) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.cases(toValue(id), toValue(version), toValue(cursor))),
    queryFn: async () => {
      const after = toValue(cursor)
      return data(
        await api.GET('/admin/datasets/{datasetId}/versions/{version}/cases', {
          params: {
            path: { datasetId: toValue(id), version: toValue(version) },
            query: { limit: '50', ...(after ? { cursor: after } : {}) },
          },
        }),
      )
    },
    placeholderData: keepPreviousData,
  })
}

/**
 * Uploads a JSONL file as the next Dataset Version. The body is the raw file (api.md section 3), which the OpenAPI document cannot
 * describe, so the file goes through the typed client with a pass-through serializer: the middleware still adds the antiforgery
 * header and turns a 400 into an ApiError carrying the line-numbered `errors`.
 */
export async function uploadDatasetVersion(datasetId: string, file: Blob) {
  return data(
    await api.POST('/admin/datasets/{datasetId}/versions', {
      params: { path: { datasetId } },
      body: file as never,
      bodySerializer: (body: unknown) => body as BodyInit,
      headers: { 'Content-Type': 'application/x-ndjson' },
    }),
  )
}

/**
 * Deletes a Dataset with its versions and Cases (evaluation.md section 8). The server answers 409 `conflict` while a Comparison
 * uses it, which rejects with that ApiError.
 */
export async function deleteDataset(datasetId: string) {
  await api.DELETE('/admin/datasets/{datasetId}', { params: { path: { datasetId } } })
}

/** Deletes a Prompt with its versions; 409 `conflict` while a Comparison has a run built from it. */
export async function deletePrompt(promptId: string) {
  await api.DELETE('/admin/prompts/{promptId}', { params: { path: { promptId } } })
}

export function usePrompts(
  tenantId: MaybeRefOrGetter<string>,
  cursor: MaybeRefOrGetter<string | null>,
) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.promptList(toValue(tenantId), toValue(cursor))),
    queryFn: async () =>
      data(
        await api.GET('/admin/prompts', {
          params: { query: listQuery(toValue(tenantId), toValue(cursor)) },
        }),
      ),
    placeholderData: keepPreviousData,
  })
}

export function usePrompt(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.prompt(toValue(id))),
    queryFn: async () =>
      data(
        await api.GET('/admin/prompts/{promptId}', { params: { path: { promptId: toValue(id) } } }),
      ),
  })
}

export function usePromptVersions(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => evaluationKeys.promptVersions(toValue(id))),
    enabled: computed(() => toValue(id) !== ''),
    queryFn: async () =>
      data(
        await api.GET('/admin/prompts/{promptId}/versions', {
          params: { path: { promptId: toValue(id) }, query: { limit: '200' } },
        }),
      ),
  })
}
