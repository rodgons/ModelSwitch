import { useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

export const providerKeys = {
  all: ['providers'] as const,
  list: ['providers', 'list'] as const,
  health: ['providers', 'health'] as const,
  models: (providerId: string) => ['providers', providerId, 'models'] as const,
  prices: (modelId: string) => ['providers', 'prices', modelId] as const,
}

export function useProviders() {
  return useQuery({
    queryKey: providerKeys.list,
    queryFn: async () =>
      data(await api.GET('/admin/providers', { params: { query: { limit: '200' } as never } })),
  })
}

export function useProviderHealth() {
  return useQuery({
    queryKey: providerKeys.health,
    queryFn: async () => data(await api.GET('/admin/provider-health')),
    refetchInterval: 15_000,
  })
}

/** "provider/model" for every Provider Model id in the catalog, from the health listing (which names them all). */
export function useModelNames() {
  const health = useProviderHealth()
  return computed(
    () =>
      new Map(
        (health.data.value?.items ?? []).map((i) => [
          i.model.id,
          `${i.model.providerName}/${i.model.name}`,
        ]),
      ),
  )
}

export function useProviderModels(providerId: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => providerKeys.models(toValue(providerId))),
    queryFn: async () =>
      data(
        await api.GET('/admin/providers/{providerId}/models', {
          params: { path: { providerId: toValue(providerId) }, query: { limit: '200' } as never },
        }),
      ),
  })
}

export function usePrices(modelId: MaybeRefOrGetter<string>) {
  return useQuery({
    queryKey: computed(() => providerKeys.prices(toValue(modelId))),
    enabled: computed(() => toValue(modelId) !== ''),
    queryFn: async () =>
      data(
        await api.GET('/admin/models/{modelId}/prices', {
          params: { path: { modelId: toValue(modelId) }, query: { limit: '50' } as never },
        }),
      ),
  })
}
