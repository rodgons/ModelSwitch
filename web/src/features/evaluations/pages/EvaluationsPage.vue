<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useSession } from '@/app/session'
import { useTenants } from '@/features/tenants/queries'
import Button from '@/shared/components/Button.vue'
import Field from '@/shared/components/Field.vue'
import SegmentedControl from '@/shared/components/SegmentedControl.vue'
import SelectInput from '@/shared/components/SelectInput.vue'
import Container from '@/shared/layout/Container.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Stack from '@/shared/layout/Stack.vue'
import Toolbar from '@/shared/layout/Toolbar.vue'
import ComparisonsSection from '../components/ComparisonsSection.vue'
import CreateDialog from '../components/CreateDialog.vue'
import DatasetsSection from '../components/DatasetsSection.vue'
import PromptsSection from '../components/PromptsSection.vue'

type Tab = 'datasets' | 'prompts' | 'comparisons'
const tabs: { value: Tab; label: string }[] = [
  { value: 'datasets', label: 'Datasets' },
  { value: 'prompts', label: 'Prompts' },
  { value: 'comparisons', label: 'Comparisons' },
]

const session = useSession()
const route = useRoute()
const router = useRouter()
const tenants = useTenants()

/** The tab and the Tenant filter live in the URL, so a view can be shared and survives a reload. */
const query = (key: string) => (typeof route.query[key] === 'string' ? route.query[key] : '')
const setQuery = (key: string, value: string) =>
  void router.replace({ query: { ...route.query, [key]: value || undefined } })
const tab = computed<Tab>({
  get: () => (tabs.some((t) => t.value === query('tab')) ? (query('tab') as Tab) : 'datasets'),
  set: (value) => setQuery('tab', value === 'datasets' ? '' : value),
})
const tenantId = computed({ get: () => query('tenantId'), set: (v) => setQuery('tenantId', v) })

const tenantOptions = computed(() => [
  { value: '', label: 'All Tenants in your grant' },
  ...(tenants.data.value?.items ?? []).map((t) => ({ value: t.id, label: t.name })),
])
const names = computed(
  () => new Map((tenants.data.value?.items ?? []).map((t) => [t.id, t.name] as const)),
)
const tenantName = (id: string) => names.value.get(id) ?? id

const createOpen = ref(false)
const canWrite = computed(() => session.can('operator'))
</script>

<template>
  <Container>
    <PageHeader
      title="Evaluations"
      lede="Datasets of Cases and versioned Prompts, run against models side by side. Every upload and every Prompt edit is a new immutable version."
    >
      <template #actions>
        <Button
          v-if="canWrite && tab === 'comparisons'"
          variant="primary"
          :to="{ path: '/evaluations/new', query: tenantId ? { tenantId } : {} }"
          >Start a run</Button
        >
        <Button v-else-if="canWrite" variant="primary" @click="createOpen = true">{{
          tab === 'datasets' ? 'Create Dataset' : 'Create Prompt'
        }}</Button>
      </template>
    </PageHeader>
    <Stack :gap="24">
      <Inline justify="between" align="end" :gap="12">
        <SegmentedControl v-model="tab" label="Evaluation section" :options="tabs" />
        <Toolbar>
          <Field v-slot="{ id }" label="Tenant"
            ><SelectInput :id="id" v-model="tenantId" :options="tenantOptions"
          /></Field>
        </Toolbar>
      </Inline>
      <DatasetsSection v-if="tab === 'datasets'" :tenant-id="tenantId" :tenant-name="tenantName" />
      <PromptsSection
        v-else-if="tab === 'prompts'"
        :tenant-id="tenantId"
        :tenant-name="tenantName"
      />
      <ComparisonsSection v-else :tenant-id="tenantId" :tenant-name="tenantName" />
    </Stack>
    <CreateDialog
      v-if="tab !== 'comparisons'"
      v-model:visible="createOpen"
      :kind="tab === 'datasets' ? 'dataset' : 'prompt'"
      :tenant-id="tenantId"
    />
  </Container>
</template>
