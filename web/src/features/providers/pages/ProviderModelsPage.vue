<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { api, type Schemas } from '@/api/client'
import { useSession } from '@/app/session'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Money from '@/shared/components/Money.vue'
import QueryView from '@/shared/components/QueryView.vue'
import { formatTimestamp } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { summarize } from '../capabilities'
import ModelFormDialog from '../components/ModelFormDialog.vue'
import PriceFormDialog from '../components/PriceFormDialog.vue'
import { providerKeys, usePrices, useProviderModels, useProviders } from '../queries'

const props = defineProps<{ id: string }>()
const session = useSession()
const queries = useQueryClient()
const providers = useProviders()
const models = useProviderModels(() => props.id)
const provider = computed(() => providers.data.value?.items.find((p) => p.id === props.id))
const selected = ref<Schemas['ProviderModelView'] | null>(null)
const prices = usePrices(() => selected.value?.id ?? '')
const editing = ref<Schemas['ProviderModelView'] | null>(null)
const modelOpen = ref(false)
const priceOpen = ref(false)
const toggleError = ref<unknown>(null)

function edit(model: Schemas['ProviderModelView'] | null) {
  editing.value = model
  modelOpen.value = true
}

async function toggle(model: Schemas['ProviderModelView']) {
  toggleError.value = null
  try {
    await api.PATCH('/admin/models/{modelId}', {
      params: { path: { modelId: model.id } },
      body: { displayName: null, capabilities: null, enabled: !model.enabled },
    })
    await queries.invalidateQueries({ queryKey: providerKeys.all })
  } catch (error) {
    toggleError.value = error
  }
}

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  back: stylex.props(link.quiet, link.focus),
}
const rowAttrs = (model: Schemas['ProviderModelView']) =>
  stylex.props(table.row, selected.value?.id === model.id && table.highlighted)
</script>

<template>
  <Container>
    <PageHeader
      :title="provider ? `${provider.name} models` : 'Provider Models'"
      lede="The concrete models this Provider offers, their Capabilities and their Price history."
      :back="{ to: '/providers', label: 'All Providers' }"
    >
      <template v-if="session.can('owner')" #actions>
        <Button variant="primary" @click="edit(null)">Add model</Button>
      </template>
    </PageHeader>
    <Stack :gap="48">
      <ErrorState v-if="toggleError" :error="toggleError" />
      <Section title="Provider Models" surface flush>
        <QueryView
          :data="models.data.value"
          :is-pending="models.isPending.value"
          :error="models.error.value"
          :refetch="models.refetch"
          :is-empty="(page) => page.items.length === 0"
          empty-text="No Provider Model yet. Add the models this Provider serves, then price them."
        >
          <template #default="{ data: page }">
            <TableScroll label="Provider Models">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Model id</th>
                    <th v-bind="s.th" scope="col">Display name</th>
                    <th v-bind="s.th" scope="col">Capabilities</th>
                    <th v-bind="s.th" scope="col">State</th>
                    <th v-bind="s.th" scope="col">Actions</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="model in page.items" :key="model.id" v-bind="rowAttrs(model)">
                    <td v-bind="s.td">
                      <span v-bind="s.mono">{{ model.providerModelId }}</span>
                    </td>
                    <td v-bind="s.td">{{ model.displayName }}</td>
                    <td v-bind="s.td">{{ summarize(model.capabilities) }}</td>
                    <td v-bind="s.td">
                      <Badge :tone="model.enabled ? 'success' : 'neutral'">{{
                        model.enabled ? 'Enabled' : 'Disabled'
                      }}</Badge>
                    </td>
                    <td v-bind="s.td">
                      <Inline :gap="4" :wrap="false">
                        <Button
                          size="sm"
                          variant="ghost"
                          :aria-pressed="selected?.id === model.id"
                          @click="selected = model"
                          >Prices</Button
                        >
                        <template v-if="session.can('owner')">
                          <Button size="sm" variant="ghost" @click="edit(model)">Edit</Button>
                          <Button size="sm" variant="ghost" @click="toggle(model)">{{
                            model.enabled ? 'Disable' : 'Enable'
                          }}</Button>
                        </template>
                      </Inline>
                    </td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
          </template>
        </QueryView>
      </Section>
      <Section v-if="selected" :title="`Prices of ${selected.providerModelId}`" surface flush>
        <template #actions
          ><Button v-if="session.can('owner')" size="sm" @click="priceOpen = true"
            >Add price</Button
          ></template
        >
        <QueryView
          :data="prices.data.value"
          :is-pending="prices.isPending.value"
          :error="prices.error.value"
          :refetch="prices.refetch"
          :is-empty="(page) => page.items.length === 0"
          empty-text="No Price yet: Attempts on this model cost nothing until one is added, so the model is not eligible for cost limits."
        >
          <template #default="{ data: page }">
            <TableScroll label="Price history">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Effective from</th>
                    <th v-bind="s.thNumber" scope="col">Input / 1M</th>
                    <th v-bind="s.thNumber" scope="col">Cached input / 1M</th>
                    <th v-bind="s.thNumber" scope="col">Output / 1M</th>
                    <th v-bind="s.th" scope="col">State</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="price in page.items" :key="price.id">
                    <td v-bind="s.td">{{ formatTimestamp(price.effectiveFrom) }}</td>
                    <td v-bind="s.number">
                      <Money :amount="price.inputPerMillion" :estimated="false" />
                    </td>
                    <td v-bind="s.number">
                      <Money
                        v-if="price.cachedInputPerMillion !== null"
                        :amount="price.cachedInputPerMillion"
                        :estimated="false"
                      /><span v-else>Input rate</span>
                    </td>
                    <td v-bind="s.number">
                      <Money :amount="price.outputPerMillion" :estimated="false" />
                    </td>
                    <td v-bind="s.td">
                      <Badge v-if="price.inForce" tone="success">In force</Badge>
                    </td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
          </template>
        </QueryView>
        <Inline :gap="6"
          ><EstimatedBadge />Costs computed from these Prices are estimates, not the Provider's
          bill.</Inline
        >
      </Section>
    </Stack>
    <ModelFormDialog v-model:visible="modelOpen" :provider-id="id" :model="editing" />
    <PriceFormDialog
      v-if="selected"
      v-model:visible="priceOpen"
      :model-id="selected.id"
      :model-name="selected.providerModelId"
    />
  </Container>
</template>
