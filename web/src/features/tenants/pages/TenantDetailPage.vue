<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, reactive, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { api, data } from '@/api/client'
import { useSession } from '@/app/session'
import Alert from '@/shared/components/Alert.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import ConfirmDialog from '@/shared/components/ConfirmDialog.vue'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import ErrorState from '@/shared/components/ErrorState.vue'
import Field from '@/shared/components/Field.vue'
import Money from '@/shared/components/Money.vue'
import QueryView from '@/shared/components/QueryView.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import TextInput from '@/shared/components/TextInput.vue'
import { formatPercent, formatTokens } from '@/shared/format'
import { useFormMutation } from '@/shared/forms'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Grid from '@/shared/layout/Grid.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { budgetUse, monthStart, parseAliases } from '../budget'
import ApiKeysSection from '../components/ApiKeysSection.vue'
import { tenantKeys, useTenant, useTenantUsage } from '../queries'
import { gauge } from '../tenants.styles'

const props = defineProps<{ id: string }>()
const session = useSession()
const tenant = useTenant(() => props.id)
const usage = useTenantUsage(() => props.id, monthStart(new Date()))
const canEdit = computed(() => session.can('operator'))

const limits = reactive({
  requestsPerMinute: '',
  maxConcurrent: '',
  maxRequestCost: '',
  budget: '',
})
const access = ref('')
watch(
  () => tenant.data.value,
  (value) => {
    if (!value) return
    const l = value.limits
    Object.assign(limits, {
      requestsPerMinute: l.requestsPerMinute?.toString() ?? '',
      maxConcurrent: l.maxConcurrent?.toString() ?? '',
      maxRequestCost: l.maxRequestCost?.toString() ?? '',
      budget: l.budget?.amount.toString() ?? '',
    })
    access.value = value.modelAccess.allowedAliases.join(', ')
  },
  { immediate: true },
)

const number = (value: string) => (value.trim() ? Number(value) : null)
const saveLimits = useFormMutation(
  async () =>
    data(
      await api.PUT('/admin/tenants/{tenantId}/limits', {
        params: { path: { tenantId: props.id } },
        body: {
          requestsPerMinute: number(limits.requestsPerMinute),
          maxConcurrent: number(limits.maxConcurrent),
          maxRequestCost: number(limits.maxRequestCost),
          budget: limits.budget.trim() ? { amount: Number(limits.budget), period: 'month' } : null,
        },
      }),
    ),
  [tenantKeys.detail(props.id)],
)
const saveAccess = useFormMutation(
  async () =>
    data(
      await api.PUT('/admin/tenants/{tenantId}/model-access', {
        params: { path: { tenantId: props.id } },
        body: { allowedAliases: parseAliases(access.value) },
      }),
    ),
  [tenantKeys.detail(props.id)],
)

const statusOpen = ref(false)
const changeStatus = useFormMutation(
  async () =>
    data(
      await api.PATCH('/admin/tenants/{tenantId}', {
        params: { path: { tenantId: props.id } },
        body: {
          name: null,
          status: tenant.data.value?.status === 'active' ? 'suspended' : 'active',
        },
      }),
    ),
  [tenantKeys.all],
  () => (statusOpen.value = false),
)

// The current Budget period as the Reservation sees it: settled Estimated Cost plus what requests in flight hold.
const budget = computed(() => {
  const period = tenant.data.value?.budgetPeriod
  return budgetUse(period?.spent ?? 0, period?.reserved ?? 0, period?.limit)
})
const percent = (share: number) => `${(share * 100).toFixed(1)}%`
const spentAttrs = computed(() =>
  stylex.props(gauge.segment(percent(budget.value.spentShare)), gauge[budget.value.tone]),
)
const reservedAttrs = computed(() =>
  stylex.props(gauge.segment(percent(budget.value.reservedShare)), gauge.reserved),
)
const spentSwatch = computed(() => stylex.props(gauge.swatch, gauge[budget.value.tone]))
const s = {
  track: stylex.props(gauge.track),
  reservedSwatch: stylex.props(gauge.swatch, gauge.reserved),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  back: stylex.props(link.quiet, link.focus),
  caption: stylex.props(typo.caption),
}
</script>

<template>
  <Container>
    <QueryView
      :data="tenant.data.value"
      :is-pending="tenant.isPending.value"
      :error="tenant.error.value"
      :refetch="tenant.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader :title="detail.name" :back="{ to: '/tenants', label: 'All Tenants' }">
          <template #lede><StatusBadge :status="detail.status" /></template>
          <template #actions>
            <Inline :gap="12">
              <RouterLink
                :to="{ path: '/requests', query: { tenantId: detail.id } }"
                v-bind="s.back"
                >This Tenant's requests</RouterLink
              >
              <Button
                v-if="canEdit"
                :destructive="detail.status === 'active'"
                @click="statusOpen = true"
                >{{ detail.status === 'active' ? 'Suspend Tenant' : 'Activate Tenant' }}</Button
              >
            </Inline>
          </template>
        </PageHeader>
        <Stack :gap="48">
          <Facts
            :items="[
              { label: 'Tenant id', value: detail.id },
              { label: 'Model access', value: detail.modelAccess.allowedAliases.join(', ') },
            ]"
          />
          <Split>
            <Stack :gap="48">
              <Section title="Limits and Budget" surface>
                <form novalidate @submit.prevent="saveLimits.mutation.mutate(undefined)">
                  <Stack :gap="16">
                    <ErrorState
                      v-if="saveLimits.generalError.value"
                      :error="saveLimits.generalError.value"
                    />
                    <Alert v-if="saveLimits.mutation.isSuccess.value" tone="success"
                      >Limits saved. They apply to the next request on every instance.</Alert
                    >
                    <Grid min="field" :gap="12">
                      <Field
                        v-slot="{ id: fieldId, invalid }"
                        label="Requests per minute"
                        help="Empty: unlimited."
                        :errors="saveLimits.errorsFor('requestsPerMinute')"
                      >
                        <TextInput
                          :id="fieldId"
                          v-model="limits.requestsPerMinute"
                          type="number"
                          :invalid="invalid"
                        />
                      </Field>
                      <Field
                        v-slot="{ id: fieldId, invalid }"
                        label="Concurrent requests"
                        help="Empty: unlimited."
                        :errors="saveLimits.errorsFor('maxConcurrent')"
                      >
                        <TextInput
                          :id="fieldId"
                          v-model="limits.maxConcurrent"
                          type="number"
                          :invalid="invalid"
                        />
                      </Field>
                      <Field
                        v-slot="{ id: fieldId, invalid }"
                        label="Max Estimated Cost per request (USD)"
                        help="Empty: no cap."
                        :errors="saveLimits.errorsFor('maxRequestCost')"
                      >
                        <TextInput
                          :id="fieldId"
                          v-model="limits.maxRequestCost"
                          inputmode="decimal"
                          :invalid="invalid"
                        />
                      </Field>
                      <Field
                        v-slot="{ id: fieldId, invalid }"
                        label="Monthly Budget (USD)"
                        help="Spending limit of Estimated Cost per calendar month (UTC). Empty: none."
                        :errors="saveLimits.errorsFor('budget.amount')"
                      >
                        <TextInput
                          :id="fieldId"
                          v-model="limits.budget"
                          inputmode="decimal"
                          :invalid="invalid"
                        />
                      </Field>
                    </Grid>
                    <div v-if="canEdit">
                      <Button
                        type="submit"
                        variant="primary"
                        :loading="saveLimits.mutation.isPending.value"
                        >Save limits</Button
                      >
                    </div>
                  </Stack>
                </form>
              </Section>
              <Section title="Model access" surface>
                <form novalidate @submit.prevent="saveAccess.mutation.mutate(undefined)">
                  <Stack :gap="16">
                    <ErrorState
                      v-if="saveAccess.generalError.value"
                      :error="saveAccess.generalError.value"
                    />
                    <Field
                      v-slot="{ id: fieldId, invalid }"
                      label="Allowed Model Aliases"
                      help="Comma-separated, or * for every alias. To block a Tenant, suspend it."
                      :errors="saveAccess.errorsFor('allowedAliases')"
                    >
                      <TextInput :id="fieldId" v-model="access" mono :invalid="invalid" />
                    </Field>
                    <div v-if="canEdit">
                      <Button
                        type="submit"
                        variant="primary"
                        :loading="saveAccess.mutation.isPending.value"
                        >Save model access</Button
                      >
                    </div>
                  </Stack>
                </form>
              </Section>
              <ApiKeysSection :tenant-id="id" :can-edit="canEdit" />
            </Stack>
            <template #aside>
              <Stack :gap="48">
                <Section title="Budget this month">
                  <Stack :gap="8">
                    <div
                      v-if="budget.ratio !== null"
                      v-bind="s.track"
                      role="meter"
                      :aria-valuenow="Math.round(budget.ratio * 100)"
                      aria-valuemin="0"
                      aria-valuemax="100"
                      :aria-valuetext="`${formatPercent(budget.ratio)} of the Budget spent or reserved`"
                      aria-label="Budget used"
                    >
                      <div v-bind="spentAttrs" />
                      <div v-bind="reservedAttrs" />
                    </div>
                    <Facts
                      :items="[
                        { label: 'Spent', value: '', key: 'spent' },
                        { label: 'Reserved', value: '', key: 'reserved' },
                        { label: 'Limit', value: '', key: 'limit' },
                      ]"
                    >
                      <template #spent
                        ><span v-if="budget.ratio !== null" v-bind="spentSwatch" /><Money
                          :amount="detail.budgetPeriod.spent"
                          estimated
                      /></template>
                      <template #reserved
                        ><span v-if="budget.ratio !== null" v-bind="s.reservedSwatch" /><Money
                          :amount="detail.budgetPeriod.reserved"
                          estimated
                      /></template>
                      <template #limit>
                        <Money
                          v-if="detail.budgetPeriod.limit !== null"
                          :amount="detail.budgetPeriod.limit"
                          :estimated="false"
                        />
                        <span v-else>None</span>
                      </template>
                    </Facts>
                    <span v-bind="s.caption">{{
                      budget.ratio === null
                        ? 'No Budget: spending is not capped.'
                        : `${formatPercent(budget.ratio)} of the Budget spent or reserved. Reserved is held by requests in flight until they settle.`
                    }}</span>
                  </Stack>
                </Section>
                <Section title="Usage this month" surface flush>
                  <QueryView
                    :data="usage.data.value"
                    :is-pending="usage.isPending.value"
                    :error="usage.error.value"
                    :refetch="usage.refetch"
                    :is-empty="(report) => report.items.length === 0"
                    empty-text="No request this month."
                  >
                    <template #default="{ data: report }">
                      <TableScroll label="Usage by Model Alias">
                        <table v-bind="s.table">
                          <thead>
                            <tr>
                              <th v-bind="s.th" scope="col">Model Alias</th>
                              <th v-bind="s.thNumber" scope="col">Requests</th>
                              <th v-bind="s.thNumber" scope="col">
                                <Inline :gap="6" justify="end" :wrap="false" as="span"
                                  >Cost <EstimatedBadge
                                /></Inline>
                              </th>
                            </tr>
                          </thead>
                          <tbody>
                            <tr v-for="row in report.items" :key="row.alias ?? ''">
                              <td v-bind="s.td">
                                <span v-bind="s.mono">{{ row.alias }}</span>
                              </td>
                              <td v-bind="s.number">{{ formatTokens(row.requests) }}</td>
                              <td v-bind="s.number">
                                <Money :amount="row.estimatedCost" estimated :badge="false" />
                              </td>
                            </tr>
                          </tbody>
                        </table>
                      </TableScroll>
                    </template>
                  </QueryView>
                </Section>
              </Stack>
            </template>
          </Split>
        </Stack>
        <ConfirmDialog
          v-model:visible="statusOpen"
          :title="
            detail.status === 'active' ? `Suspend ${detail.name}?` : `Activate ${detail.name}?`
          "
          :action="detail.status === 'active' ? 'Suspend Tenant' : 'Activate Tenant'"
          :confirm-text="detail.status === 'active' ? detail.name : undefined"
          :pending="changeStatus.mutation.isPending.value"
          @confirm="changeStatus.mutation.mutate(undefined)"
        >
          <p v-if="detail.status === 'active'">
            Every API Key of this Tenant will get 403 <code>tenant_suspended</code> until it is
            activated again.
          </p>
          <p v-else>Its API Keys will work again.</p>
          <ErrorState
            v-if="changeStatus.generalError.value"
            :error="changeStatus.generalError.value"
          />
        </ConfirmDialog>
      </template>
    </QueryView>
  </Container>
</template>
