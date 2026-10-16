import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

/** The Request Explorer's filters, exactly as the URL query string carries them (frontend.md section 2). */
export interface RequestFilters {
  from?: string
  to?: string
  tenantId?: string
  alias?: string
  providerId?: string
  modelId?: string
  status?: string
  errorCode?: string
  hadFallback?: string
  sort?: string
  order?: string
  cursor?: string
}

export const requestKeys = {
  list: (filters: RequestFilters) => ['requests', 'list', filters] as const,
  detail: (id: string) => ['requests', 'detail', id] as const,
}

export function useRequests(filters: MaybeRefOrGetter<RequestFilters>) {
  return useQuery({
    queryKey: computed(() => requestKeys.list(toValue(filters))),
    queryFn: async () =>
      data(
        await api.GET('/admin/requests', {
          params: { query: { ...toValue(filters), limit: '50' } as never },
        }),
      ),
    placeholderData: keepPreviousData,
  })
}

export function useRequest(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => requestKeys.detail(toValue(id))),
    queryFn: async () =>
      data(
        await api.GET('/admin/requests/{requestId}', {
          params: { path: { requestId: toValue(id) } },
        }),
      ),
  })
}
