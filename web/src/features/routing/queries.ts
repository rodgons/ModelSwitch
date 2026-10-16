import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

export const routingKeys = {
  all: ['routing'] as const,
  policies: ['routing', 'policies'] as const,
  versions: (policyId: string) => ['routing', 'versions', policyId] as const,
}

export function usePolicies() {
  return useQuery({
    queryKey: routingKeys.policies,
    queryFn: async () =>
      data(
        await api.GET('/admin/routing-policies', { params: { query: { limit: '200' } as never } }),
      ),
  })
}

export function useVersions(policyId: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => routingKeys.versions(toValue(policyId))),
    enabled: computed(() => toValue(policyId) !== ''),
    queryFn: async () =>
      data(
        await api.GET('/admin/routing-policies/{policyId}/versions', {
          params: { path: { policyId: toValue(policyId) }, query: { limit: '50' } as never },
        }),
      ),
  })
}
