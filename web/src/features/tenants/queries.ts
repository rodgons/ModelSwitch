import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

export const tenantKeys = {
  all: ['tenants'] as const,
  list: ['tenants', 'list'] as const,
  detail: (id: string) => ['tenants', 'detail', id] as const,
  keys: (id: string) => ['tenants', 'keys', id] as const,
  usage: (id: string, from: string) => ['tenants', 'usage', id, from] as const,
}

export function useTenants() {
  return useQuery({
    queryKey: tenantKeys.list,
    queryFn: async () =>
      data(await api.GET('/admin/tenants', { params: { query: { limit: '200' } as never } })),
  })
}

export function useTenant(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => tenantKeys.detail(toValue(id))),
    queryFn: async () =>
      data(
        await api.GET('/admin/tenants/{tenantId}', { params: { path: { tenantId: toValue(id) } } }),
      ),
  })
}

export function useApiKeys(id: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => tenantKeys.keys(toValue(id))),
    queryFn: async () =>
      data(
        await api.GET('/admin/tenants/{tenantId}/api-keys', {
          params: { path: { tenantId: toValue(id) }, query: { limit: '200' } as never },
        }),
      ),
  })
}

/** Estimated Cost and traffic by Model Alias since `from` (the start of the month for the Budget). */
export function useTenantUsage(id: MaybeRefOrGetter<string>, from: string) {
  return useQuery({
    queryKey: computed(() => tenantKeys.usage(toValue(id), from)),
    queryFn: async () =>
      data(
        await api.GET('/admin/usage', {
          params: {
            query: { tenantId: toValue(id), from, interval: 'month', groupBy: 'alias' } as never,
          },
        }),
      ),
  })
}
