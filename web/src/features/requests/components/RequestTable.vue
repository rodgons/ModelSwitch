<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { RouterLink } from 'vue-router'
import type { Schemas } from '@/api/client'
import { link, table, typo } from '@/shared/components/components.styles'
import EstimatedBadge from '@/shared/components/EstimatedBadge.vue'
import Money from '@/shared/components/Money.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import { formatDuration, formatRelative, formatTokens, truncateMiddle } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import RouteStrip from '@/shared/route/RouteStrip.vue'
import { modelName, stripAttempts } from '../strip'

/** Inference Requests as rows: id, alias, mini Route Strip, outcome, model, timing, tokens and Estimated Cost. */
withDefaults(
  defineProps<{ items: Schemas['RequestSummaryView'][]; label: string; compact?: boolean }>(),
  { compact: false },
)
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thPinned: stylex.props(table.th, table.pinned),
  tdPinned: stylex.props(table.td, table.pinned),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  tdNoWrap: stylex.props(table.td, table.noWrap),
  number: stylex.props(table.td, table.number),
  row: stylex.props(table.row),
  id: stylex.props(link.quiet, link.focus, typo.mono),
  mono: stylex.props(typo.mono),
  muted: stylex.props(typo.caption),
}
</script>

<template>
  <TableScroll :label="label">
    <table v-bind="s.table">
      <thead>
        <tr>
          <th v-bind="s.thPinned" scope="col">Request</th>
          <th v-bind="s.th" scope="col">Model Alias</th>
          <th v-bind="s.th" scope="col">Route</th>
          <th v-bind="s.th" scope="col">Status</th>
          <th v-if="!compact" v-bind="s.th" scope="col">Provider Model</th>
          <th v-bind="s.thNumber" scope="col">Duration</th>
          <th v-if="!compact" v-bind="s.thNumber" scope="col">Tokens in / out</th>
          <th v-if="!compact" v-bind="s.thNumber" scope="col">
            <Inline :gap="6" justify="end" :wrap="false" as="span">Cost <EstimatedBadge /></Inline>
          </th>
          <th v-bind="s.th" scope="col">Started</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="request in items" :key="request.id" v-bind="s.row">
          <td v-bind="s.tdPinned">
            <RouterLink :to="`/requests/${request.id}`" v-bind="s.id" :title="request.id">{{
              truncateMiddle(request.id)
            }}</RouterLink>
          </td>
          <td v-bind="s.td">
            <span v-bind="s.mono">{{ request.requestedAlias }}</span>
          </td>
          <td v-bind="s.td">
            <RouteStrip
              v-if="request.attempts.length"
              variant="mini"
              :started-at="request.startedAt"
              :duration-ms="request.durationMs"
              :attempts="stripAttempts(request.attempts)"
            />
            <span v-else v-bind="s.muted">No Attempt</span>
          </td>
          <td v-bind="s.td">
            <Inline :gap="6" :wrap="false" as="span">
              <StatusBadge :status="request.status" />
              <span v-if="request.errorCode" v-bind="s.mono">{{ request.errorCode }}</span>
            </Inline>
          </td>
          <td v-if="!compact" v-bind="s.tdNoWrap">
            <span v-bind="s.mono">{{ modelName(request.selectedModel) }}</span>
          </td>
          <td v-bind="s.number">{{ formatDuration(request.durationMs) }}</td>
          <td v-if="!compact" v-bind="s.number">
            {{ formatTokens(request.totalInputTokens) }} /
            {{ formatTokens(request.totalOutputTokens) }}
          </td>
          <td v-if="!compact" v-bind="s.number">
            <Money :amount="request.totalEstimatedCost" estimated :badge="false" />
          </td>
          <td v-bind="s.td">
            <span :title="request.startedAt">{{ formatRelative(request.startedAt) }}</span>
          </td>
        </tr>
      </tbody>
    </table>
  </TableScroll>
</template>
