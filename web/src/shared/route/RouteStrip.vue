<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed, ref } from 'vue'
import Button from '@/shared/components/Button.vue'
import { table, typo } from '@/shared/components/components.styles'
import { formatDuration, truncateMiddle } from '@/shared/format'
import Inline from '@/shared/layout/Inline.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import { fullScale, layoutRoute, miniScale, type StripAttempt } from './routeStrip'
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
const full = computed(() => props.variant === 'full')
const route = computed(() =>
  layoutRoute(
    props.startedAt,
    props.durationMs,
    props.attempts,
    full.value ? fullScale : miniScale,
  ),
)
const pct = (share: number) => `${(share * 100).toFixed(3)}%`
const trackLabel = computed(() => {
  const count = route.value.segments.length
  const name = `Route of the request, ${count} ${count === 1 ? 'Attempt' : 'Attempts'}`
  return route.value.toScale ? name : `${name}, long waits drawn shorter`
})

const s = computed(() => ({
  root: stylex.props(strip.root),
  track: stylex.props(strip.track, full.value ? strip.full : strip.mini),
  ticks: stylex.props(strip.ticks),
  brk: stylex.props(strip.brk),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
}))
const itemAttrs = (x: number, w: number) => stylex.props(strip.item(pct(x), pct(w)))
const barAttrs = (outcome: keyof typeof strip, x: number, attempt: number) =>
  stylex.props(
    strip.bar,
    full.value ? strip.barFull : strip.barMini,
    strip[outcome as 'succeeded'],
    full.value && strip.drawn(`${Math.round(x * 360)}ms`),
    props.highlighted === attempt && strip.emphasised,
  )
const labelAttrs = (start: number, width: number, end: boolean) =>
  stylex.props(strip.label(pct(start), pct(width)), end && strip.labelEnd)
const tickAttrs = (at: number) =>
  stylex.props(
    strip.tick(pct(at)),
    at === 0 ? strip.tickStart : at === 1 ? strip.tickEnd : strip.tickMiddle,
  )
const glyphAttrs = (outcome: keyof typeof strip) =>
  // The outcome style sets the color; the variant style after it takes back the fill, so the glyph reads as a shape, not a block.
  stylex.props(
    strip.glyph,
    strip[outcome as 'succeeded'],
    full.value ? strip.glyphFull : strip.glyphMini,
  )
const gapAttrs = (x: number, w: number) =>
  stylex.props(strip.gap(pct(x), pct(w)), full.value ? strip.gapFull : strip.gapMini)
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
      <ol v-bind="s.track" :aria-label="trackLabel">
        <li
          v-for="gap in route.gaps"
          :key="`gap-${gap.offsetMs}`"
          v-bind="gapAttrs(gap.x, gap.w)"
          aria-hidden="true"
        >
          <span v-if="gap.shortened" v-bind="s.brk" />
        </li>
        <template v-if="full">
          <li
            v-for="segment in route.segments.filter((x) => x.labelled)"
            :key="`label-${segment.attemptNumber}`"
            v-bind="
              labelAttrs(
                segment.labelStart,
                segment.labelWidth,
                segment.attemptNumber === route.segments.at(-1)?.attemptNumber,
              )
            "
            :title="segment.model"
            aria-hidden="true"
          >
            {{ truncateMiddle(segment.model, 14, 8) }}
          </li>
        </template>
        <li
          v-for="segment in route.segments"
          :key="segment.attemptNumber"
          v-bind="itemAttrs(segment.x, segment.w)"
        >
          <span
            v-bind="barAttrs(segment.outcome, segment.x, segment.attemptNumber)"
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
        <span v-for="tick in route.ticks" :key="tick.at" v-bind="tickAttrs(tick.at)">{{
          tick.label
        }}</span>
      </div>
    </template>
  </div>
</template>
