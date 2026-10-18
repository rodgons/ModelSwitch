<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Identifier from '@/shared/components/Identifier.vue'
import Money from '@/shared/components/Money.vue'
import QueryView from '@/shared/components/QueryView.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import { formatDuration, formatTimestamp, formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Facts from '@/shared/layout/Facts.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Split from '@/shared/layout/Split.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import RouteStrip from '@/shared/route/RouteStrip.vue'
import EvaluationCaseLink from '@/features/evaluations/components/EvaluationCaseLink.vue'
import { useModelNames } from '@/features/providers/queries'
import RoutingDecisionView from '../components/RoutingDecisionView.vue'
import { useRequest } from '../queries'
import { modelName, stripAttempts } from '../strip'

/** Request detail (frontend.md section 2): what happened to this request, which Provider Model served it, and what it cost. */
const props = defineProps<{ id: string }>()
const request = useRequest(() => props.id)
const names = useModelNames()
const highlighted = ref<number | null>(null)

const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  tdNoWrap: stylex.props(table.td, table.noWrap),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  external: stylex.props(link.inline, link.focus),
  back: stylex.props(link.quiet, link.focus),
  caption: stylex.props(typo.caption),
}
const errorDetail = (attempt: {
  errorClass?: string | null
  errorCode?: string | null
  httpStatus?: number | null
  providerErrorType?: string | null
}) =>
  [
    attempt.errorClass,
    attempt.httpStatus ? `HTTP ${attempt.httpStatus}` : null,
    attempt.errorCode ? attempt.providerErrorType : null,
  ]
    .filter(Boolean)
    .join(', ')
const rowAttrs = (attempt: number) =>
  stylex.props(table.row, highlighted.value === attempt && table.highlighted)

const facts = computed(() => {
  const detail = request.data.value
  if (!detail) return []
  const r = detail.request
  return [
    { label: 'Model Alias', value: r.requestedAlias },
    { label: 'Served by', value: modelName(r.selectedModel) },
    { label: 'Attempts', value: String(r.attemptCount) },
    { label: 'Duration', value: formatDuration(r.durationMs) },
    { label: 'First Token', value: r.streaming ? formatDuration(r.ttftMs) : 'Not streamed' },
    {
      label: 'Tokens in / out',
      value: `${formatTokens(r.totalInputTokens)} / ${formatTokens(r.totalOutputTokens)}`,
    },
    { label: 'Estimated Cost', value: '', key: 'cost' },
    { label: 'Usage', value: r.usageStatus },
    { label: 'Started', value: formatTimestamp(r.startedAt) },
    { label: 'Tenant', value: r.tenantId, key: 'tenant' },
    { label: 'Origin', value: r.origin },
    { label: 'Policy Version', value: detail.policyVersion ? `v${detail.policyVersion}` : '—' },
    { label: 'Client request id', value: detail.clientRequestId ?? '—' },
    {
      label: 'Request body',
      value: `${formatTokens(detail.inputBytes)} bytes (content is not stored)`,
    },
  ]
})
</script>

<template>
  <Container>
    <QueryView
      :data="request.data.value"
      :is-pending="request.isPending.value"
      :error="request.error.value"
      :refetch="request.refetch"
    >
      <template #default="{ data: detail }">
        <PageHeader title="" :back="{ to: '/requests', label: 'All requests' }">
          <template #title
            ><Identifier :value="detail.request.id" :head="12" :tail="6" copy
          /></template>
          <template #lede>
            <Inline :gap="8">
              <StatusBadge :status="detail.request.status" />
              <span v-if="detail.request.errorCode" v-bind="s.mono">{{
                detail.request.errorCode
              }}</span>
              <Badge v-if="detail.request.hadFallback" tone="warning">Fallback used</Badge>
            </Inline>
          </template>
          <template v-if="detail.traceUrl" #actions>
            <a :href="detail.traceUrl" target="_blank" rel="noopener noreferrer" v-bind="s.external"
              >Open trace ↗</a
            >
          </template>
        </PageHeader>
        <Stack :gap="40">
          <EvaluationCaseLink
            v-if="detail.evaluationCase"
            :run-id="detail.evaluationCase.runId"
            :external-id="detail.evaluationCase.externalId"
            :role="detail.evaluationCase.role"
          />
          <Facts :items="facts">
            <template #cost
              ><Money :amount="detail.request.totalEstimatedCost" estimated
            /></template>
            <template #tenant
              ><RouterLink :to="`/tenants/${detail.request.tenantId}`" v-bind="s.back"
                ><Identifier :value="detail.request.tenantId" /></RouterLink
            ></template>
          </Facts>
          <RouteStrip
            v-if="detail.attempts.length"
            :started-at="detail.request.startedAt"
            :duration-ms="detail.request.durationMs"
            :attempts="stripAttempts(detail.attempts)"
            :highlighted="highlighted"
            @highlight="highlighted = $event"
          />
          <Alert v-else tone="info"
            >No Attempt was made: the request ended before any Provider was called ({{
              detail.request.errorCode ?? 'no error'
            }}).</Alert
          >
          <Split>
            <Stack :gap="40">
              <Section title="Attempts" surface flush>
                <TableScroll label="Attempts">
                  <table v-bind="s.table">
                    <thead>
                      <tr>
                        <th v-bind="s.th" scope="col">#</th>
                        <th v-bind="s.th" scope="col">Provider Model</th>
                        <th v-bind="s.th" scope="col">Kind</th>
                        <th v-bind="s.th" scope="col">Outcome</th>
                        <th v-bind="s.th" scope="col">Error</th>
                        <th v-bind="s.thNumber" scope="col">Duration</th>
                        <th v-bind="s.thNumber" scope="col">Tokens in / out</th>
                        <th v-bind="s.thNumber" scope="col">
                          <Inline :gap="6" justify="end" :wrap="false" as="span"
                            >Cost <EstimatedBadge
                          /></Inline>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr
                        v-for="attempt in detail.attempts"
                        :key="attempt.id"
                        v-bind="rowAttrs(attempt.attemptNumber)"
                        @mouseenter="highlighted = attempt.attemptNumber"
                        @mouseleave="highlighted = null"
                      >
                        <td v-bind="s.td">{{ attempt.attemptNumber }}</td>
                        <td v-bind="s.tdNoWrap">
                          <span v-bind="s.mono">{{ modelName(attempt.model) }}</span>
                        </td>
                        <td v-bind="s.td">{{ attempt.kind }}</td>
                        <td v-bind="s.td"><StatusBadge :status="attempt.status" /></td>
                        <td v-bind="s.tdNoWrap">
                          <!-- The code is what an Operator searches for; class, HTTP status and the Provider's own type explain it. -->
                          <Stack
                            v-if="attempt.errorCode || attempt.providerErrorType"
                            :gap="2"
                            as="span"
                          >
                            <span v-bind="s.mono">{{
                              attempt.errorCode ?? attempt.providerErrorType
                            }}</span>
                            <span v-bind="s.caption">{{ errorDetail(attempt) }}</span>
                          </Stack>
                          <span v-else>—</span>
                        </td>
                        <td v-bind="s.number">{{ formatDuration(attempt.durationMs) }}</td>
                        <td v-bind="s.number">
                          {{ formatTokens(attempt.inputTokens) }} /
                          {{ formatTokens(attempt.outputTokens) }}
                        </td>
                        <td v-bind="s.number">
                          <Money :amount="attempt.estimatedCost" estimated :badge="false" />
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </TableScroll>
              </Section>
              <Section title="Usage Records" surface flush>
                <TableScroll label="Usage Records">
                  <table v-bind="s.table">
                    <thead>
                      <tr>
                        <th v-bind="s.th" scope="col">Provider Model</th>
                        <th v-bind="s.th" scope="col">Source</th>
                        <th v-bind="s.thNumber" scope="col">Input (cached)</th>
                        <th v-bind="s.thNumber" scope="col">Output</th>
                        <th v-bind="s.thNumber" scope="col">
                          <Inline :gap="6" justify="end" :wrap="false" as="span"
                            >Total <EstimatedBadge
                          /></Inline>
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-for="record in detail.usageRecords" :key="record.id">
                        <td v-bind="s.td">
                          <span v-bind="s.mono">{{ modelName(record.model) }}</span>
                        </td>
                        <td v-bind="s.td">
                          <Inline :gap="6" :wrap="false" as="span">
                            <Badge
                              :tone="
                                record.usageSource === 'reported'
                                  ? 'success'
                                  : record.usageSource === 'estimated'
                                    ? 'info'
                                    : 'warning'
                              "
                              >{{ record.usageSource }}</Badge
                            >
                            <span v-if="record.estimateMethod" v-bind="s.mono">{{
                              record.estimateMethod
                            }}</span>
                          </Inline>
                        </td>
                        <td v-bind="s.number">
                          {{ formatTokens(record.inputTokens) }} ({{
                            formatTokens(record.cachedInputTokens)
                          }})
                        </td>
                        <td v-bind="s.number">{{ formatTokens(record.outputTokens) }}</td>
                        <td v-bind="s.number">
                          <Money :amount="record.totalCost" estimated :badge="false" />
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </TableScroll>
              </Section>
            </Stack>
            <template #aside>
              <Section title="Routing Decision">
                <RoutingDecisionView
                  :decision="detail.routingDecision"
                  :names="names"
                  :served-by="detail.request.selectedModel?.id ?? null"
                />
              </Section>
            </template>
          </Split>
        </Stack>
      </template>
    </QueryView>
  </Container>
</template>
