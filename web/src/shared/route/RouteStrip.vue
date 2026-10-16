<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref } from 'vue'
import Button from '@/shared/components/Button.vue'
import { table, typo } from '@/shared/components/components.styles'
import { formatDuration, truncateMiddle } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { layoutRoute, type StripAttempt } from './routeStrip'
import { strip } from './RouteStrip.styles'

/**
 * One request's path: every Attempt in order, how long it took and how it ended (design-system.md section 10). `full` spans the
 * Request detail and draws itself once; `mini` is a 120 px glance for table rows. Hovering a segment emits its Attempt number so the
 * page can highlight the matching row, and `highlighted` does the reverse.
 */
const props = withDefaults(
  defineProps<{
    startedAt: string
    durationMs: number | null
    attempts: StripAttempt[]
    variant?: 'full' | 'mini'
    highlighted?: number | null
  }>(),
  { variant: 'full', highlighted: null },
)
const emit = defineEmits<{ highlight: [attempt: number | null] }>()
const asTable = ref(false)
const route = computed(() => layoutRoute(props.startedAt, props.durationMs, props.attempts))
const full = computed(() => props.variant === 'full')
const pct = (ms: number) => `${((ms / route.value.totalMs) * 100).toFixed(3)}%`
const ticks = computed(() =>
  [0, 0.25, 0.5, 0.75, 1].map((f) => formatDuration(Math.round(route.value.totalMs * f))),
)

const s = computed(() => ({
  root: stylex.props(strip.root),
  track: stylex.props(strip.track, full.value ? strip.full : strip.mini),
  label: stylex.props(strip.label),
  labelEnd: stylex.props(strip.label, strip.labelEnd),
  ticks: stylex.props(strip.ticks),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
}))
const itemAttrs = (offset: number, duration: number) =>
  stylex.props(strip.item(pct(offset), pct(duration)))
const barAttrs = (outcome: keyof typeof strip, offset: number, attempt: number) =>
  stylex.props(
    strip.bar,
    full.value ? strip.barFull : strip.barMini,
    strip[outcome as 'succeeded'],
    full.value && strip.drawn(`${Math.round((offset / route.value.totalMs) * 360)}ms`),
    props.highlighted === attempt && strip.emphasised,
  )
const glyphAttrs = (outcome: keyof typeof strip) =>
  stylex.props(
    strip.glyph,
    full.value ? strip.glyphFull : strip.glyphMini,
    strip[outcome as 'succeeded'],
  )
const gapAttrs = (offset: number, duration: number) =>
  stylex.props(strip.gap(pct(offset), pct(duration)), full.value ? strip.gapFull : strip.gapMini)
</script>

<template>
  <div v-bind="s.root">
    <Inline v-if="full" justify="end">
      <Button variant="ghost" size="sm" :aria-pressed="asTable" @click="asTable = !asTable">{{
        asTable ? 'View as strip' : 'View as table'
      }}</Button>
    </Inline>
    <TableScroll v-if="full && asTable" label="Attempts in order">
      <table v-bind="s.table">
        <thead>
          <tr>
            <th v-bind="s.th">Attempt</th>
            <th v-bind="s.th">Provider Model</th>
            <th v-bind="s.th">Outcome</th>
            <th v-bind="s.th">Starts at</th>
            <th v-bind="s.th">Duration</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="segment in route.segments" :key="segment.attemptNumber">
            <td v-bind="s.td">{{ segment.attemptNumber }}</td>
            <td v-bind="s.td">
              <span v-bind="s.mono">{{ segment.model }}</span>
            </td>
            <td v-bind="s.td">
              {{ segment.glyph }} {{ segment.accessibleName.split(', ').slice(2).join(', ') }}
            </td>
            <td v-bind="s.number">{{ formatDuration(segment.offsetMs) }}</td>
            <td v-bind="s.number">{{ formatDuration(segment.durationMs) }}</td>
          </tr>
        </tbody>
      </table>
    </TableScroll>
    <template v-else>
      <ol
        v-bind="s.track"
        :aria-label="`Route of the request, ${route.segments.length} Attempt(s)`"
      >
        <li
          v-for="gap in route.gaps"
          :key="`gap-${gap.offsetMs}`"
          v-bind="gapAttrs(gap.offsetMs, gap.durationMs)"
          aria-hidden="true"
        />
        <li
          v-for="segment in route.segments"
          :key="segment.attemptNumber"
          v-bind="itemAttrs(segment.offsetMs, segment.durationMs)"
        >
          <span
            v-if="full && segment.labelled"
            v-bind="
              segment.attemptNumber === route.segments.at(-1)?.attemptNumber ? s.labelEnd : s.label
            "
            :title="segment.model"
            aria-hidden="true"
            >{{ truncateMiddle(segment.model, 14, 8) }}</span
          >
          <span
            v-bind="barAttrs(segment.outcome, segment.offsetMs, segment.attemptNumber)"
            :tabindex="full ? 0 : undefined"
            role="img"
            :aria-label="segment.accessibleName"
            :title="segment.accessibleName"
            @mouseenter="emit('highlight', segment.attemptNumber)"
            @mouseleave="emit('highlight', null)"
            @focus="emit('highlight', segment.attemptNumber)"
            @blur="emit('highlight', null)"
          />
          <span v-bind="glyphAttrs(segment.outcome)" aria-hidden="true">{{ segment.glyph }}</span>
        </li>
      </ol>
      <div v-if="full" v-bind="s.ticks" aria-hidden="true">
        <span v-for="(tick, index) in ticks" :key="index">{{ tick }}</span>
      </div>
    </template>
  </div>
</template>
