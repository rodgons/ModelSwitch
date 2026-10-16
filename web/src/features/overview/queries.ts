import { keepPreviousData, useQuery } from '@tanstack/vue-query'
import { computed, type MaybeRefOrGetter, toValue } from 'vue'
import { api, data } from '@/api/client'

export function useOverview(range: MaybeRefOrGetter<{ from: string; to: string }>) {
  return useQuery({
    queryKey: computed(() => ['overview', toValue(range)] as const),
    queryFn: async () =>
      data(await api.GET('/admin/overview', { params: { query: toValue(range) as never } })),
    placeholderData: keepPreviousData,
    refetchInterval: 30_000,
  })
}
