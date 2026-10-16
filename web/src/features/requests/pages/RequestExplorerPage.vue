<script setup lang="ts">
import { useQuery } from '@tanstack/vue-query'
import { computed, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api, data } from '@/api/client'
import Button from '@/shared/components/Button.vue'
import Field from '@/shared/components/Field.vue'
import QueryView from '@/shared/components/QueryView.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import TextInput from '@/shared/components/TextInput.vue'
import Container from '@/shared/layout/Container.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import RequestTable from '../components/RequestTable.vue'
import { type RequestFilters, useRequests } from '../queries'

const route = useRoute()
const router = useRouter()
const keys = [
  'from',
  'to',
  'tenantId',
  'alias',
  'providerId',
  'modelId',
  'status',
  'errorCode',
  'hadFallback',
  'sort',
  'order',
  'cursor',
] as const

/** The filters live in the URL, so a filtered view can be shared and survives a reload. */
const filters = computed<RequestFilters>(() =>
  Object.fromEntries(
    keys.flatMap((k) =>
      typeof route.query[k] === 'string' && route.query[k] ? [[k, route.query[k]]] : [],
    ),
  ),
)
const draft = reactive<Record<(typeof keys)[number], string>>(
  Object.fromEntries(keys.map((k) => [k, ''])) as Record<(typeof keys)[number], string>,
)
watch(filters, (value) => keys.forEach((k) => (draft[k] = value[k] ?? '')), { immediate: true })

const cursors = computed(() =>
  typeof route.query.back === 'string' && route.query.back ? route.query.back.split(',') : [],
)
const requests = useRequests(filters)
const tenants = useQuery({
  queryKey: ['tenants', 'all'],
  queryFn: async () =>
    data(await api.GET('/admin/tenants', { params: { query: { limit: '200' } as never } })),
})
const providers = useQuery({
  queryKey: ['providers', 'all'],
  queryFn: async () =>
    data(await api.GET('/admin/providers', { params: { query: { limit: '200' } as never } })),
})

const tenantOptions = computed(() => [
  { value: '', label: 'All Tenants' },
  ...(tenants.data.value?.items ?? []).map((t) => ({ value: t.id, label: t.name })),
])
const providerOptions = computed(() => [
  { value: '', label: 'All Providers' },
  ...(providers.data.value?.items ?? []).map((p) => ({ value: p.id, label: p.name })),
])
const statusOptions = [
  { value: '', label: 'Any status' },
  { value: 'succeeded', label: 'Succeeded' },
  { value: 'failed', label: 'Failed (incl. abandoned)' },
  { value: 'cancelled', label: 'Cancelled' },
  { value: 'abandoned', label: 'Abandoned' },
  { value: 'running', label: 'Running' },
]
const fallbackOptions = [
  { value: '', label: 'Any' },
  { value: 'true', label: 'With a Fallback' },
  { value: 'false', label: 'Without a Fallback' },
]
const sortOptions = [
  { value: '', label: 'Newest first' },
  { value: 'startedAt:asc', label: 'Oldest first' },
  { value: 'durationMs:desc', label: 'Slowest first' },
  { value: 'durationMs:asc', label: 'Fastest first' },
]
const sort = computed({
  get: () => (draft.sort ? `${draft.sort}:${draft.order || 'desc'}` : ''),
  set: (value: string) => {
    const [field, order] = value ? value.split(':') : ['', '']
    draft.sort = field ?? ''
    draft.order = order ?? ''
  },
})

function apply(extra: Record<string, string> = {}) {
  const query = Object.fromEntries(
    Object.entries({ ...draft, cursor: '', ...extra }).filter(([, v]) => v),
  )
  void router.replace({ query })
}

function clear() {
  keys.forEach((k) => (draft[k] = ''))
  apply()
}

function next(cursor: string) {
  apply({ cursor, back: [...cursors.value, filters.value.cursor ?? ''].join(',') })
}

function previous() {
  const stack = [...cursors.value]
  const cursor = stack.pop() ?? ''
  apply({ cursor, back: stack.join(',') })
}
</script>

<template>
  <Container size="wide">
    <PageHeader
      title="Requests"
      lede="Every Inference Request the gateway recorded, newest first. Prompt and completion content is never stored."
    />
    <Stack :gap="24">
      <form @submit.prevent="apply()">
        <Toolbar>
          <Field v-slot="{ id }" label="From (UTC)"
            ><TextInput :id="id" v-model="draft.from" type="datetime-local"
          /></Field>
          <Field v-slot="{ id }" label="To (UTC)"
            ><TextInput :id="id" v-model="draft.to" type="datetime-local"
          /></Field>
          <Field v-slot="{ id }" label="Tenant"
            ><SelectInput :id="id" v-model="draft.tenantId" :options="tenantOptions"
          /></Field>
          <Field v-slot="{ id }" label="Model Alias"
            ><TextInput :id="id" v-model="draft.alias" mono
          /></Field>
          <Field v-slot="{ id }" label="Provider"
            ><SelectInput :id="id" v-model="draft.providerId" :options="providerOptions"
          /></Field>
          <Field v-slot="{ id }" label="Provider Model id"
            ><TextInput :id="id" v-model="draft.modelId" mono
          /></Field>
          <Field v-slot="{ id }" label="Status"
            ><SelectInput :id="id" v-model="draft.status" :options="statusOptions"
          /></Field>
          <Field v-slot="{ id }" label="Error code"
            ><TextInput :id="id" v-model="draft.errorCode" mono
          /></Field>
          <Field v-slot="{ id }" label="Fallback"
            ><SelectInput :id="id" v-model="draft.hadFallback" :options="fallbackOptions"
          /></Field>
          <Field v-slot="{ id }" label="Order"
            ><SelectInput :id="id" v-model="sort" :options="sortOptions"
          /></Field>
          <Button type="submit" variant="primary">Apply filters</Button>
          <Button @click="clear">Clear</Button>
        </Toolbar>
      </form>
      <Section title="Results" surface flush>
        <QueryView
          :data="requests.data.value"
          :is-pending="requests.isPending.value"
          :error="requests.error.value"
          :refetch="requests.refetch"
          :is-empty="(page) => page.items.length === 0"
          empty-text="No request matches these filters. Widen the time range or clear a filter."
        >
          <template #default="{ data: page }">
            <RequestTable :items="page.items" label="Inference Requests" />
          </template>
        </QueryView>
      </Section>
      <Inline justify="end" :gap="8">
        <Button :disabled="!cursors.length && !filters.cursor" @click="previous"
          >Previous page</Button
        >
        <Button
          :disabled="!requests.data.value?.nextCursor"
          @click="next(requests.data.value?.nextCursor ?? '')"
          >Next page</Button
        >
      </Inline>
    </Stack>
  </Container>
</template>
