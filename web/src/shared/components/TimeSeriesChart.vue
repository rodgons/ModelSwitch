<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { LineChart } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, ref } from 'vue'
import VChart from 'vue-echarts'
import { theme } from '@/app/theme'
import { chartBase, chartPalette } from '@/shared/design/chart'
import Inline from '@/shared/layout/Inline.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import Button from './Button.vue'
import { chart, table } from './components.styles'

use([LineChart, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer])

/** A time series with a data-table alternative, as every chart has (frontend.md section 3). */
const props = defineProps<{
  label: string
  series: { name: string; points: [string, number | null][] }[]
  format: (value: number | null) => string
  height?: number
}>()
const asTable = ref(false)
const dark = computed(
  () =>
    theme.value === 'dark' ||
    (theme.value === 'system' &&
      typeof matchMedia !== 'undefined' &&
      matchMedia('(prefers-color-scheme: dark)').matches),
)
const option = computed(() => {
  const palette = dark.value ? chartPalette.dark : chartPalette.light
  const base = chartBase(palette)
  return {
    ...base,
    color: palette.series,
    animation: false,
    grid: { left: 8, right: 16, top: 32, bottom: 8, containLabel: true },
    // The right inset keeps the legend clear of the "View as table" toggle in the same row.
    legend: {
      type: 'scroll',
      top: 0,
      left: 0,
      right: 112,
      itemGap: 16,
      textStyle: { color: palette.text, fontFamily: base.textStyle.fontFamily },
    },
    tooltip: {
      ...base.tooltip,
      trigger: 'axis',
      valueFormatter: (value: number | null) => props.format(value),
    },
    xAxis: {
      type: 'time',
      axisLine: { lineStyle: { color: palette.grid } },
      axisLabel: { color: palette.axis },
    },
    yAxis: {
      type: 'value',
      axisLabel: { color: palette.axis, formatter: (value: number) => props.format(value) },
      splitLine: { lineStyle: { color: palette.grid } },
    },
    series: props.series.map((s) => ({
      name: s.name,
      type: 'line',
      showSymbol: s.points.filter(([, v]) => v !== null).length < 3,
      data: s.points,
      connectNulls: false,
    })),
  }
})
const rows = computed(() =>
  (props.series[0]?.points ?? []).map(([time], index) => ({
    time,
    values: props.series.map((s) => s.points[index]?.[1] ?? null),
  })),
)
const s = {
  frame: stylex.props(chart.frame),
  toggle: stylex.props(chart.toggle),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
}
</script>

<template>
  <div v-bind="s.frame">
    <Inline justify="end" v-bind="asTable ? {} : s.toggle">
      <Button variant="ghost" size="sm" :aria-pressed="asTable" @click="asTable = !asTable">{{
        asTable ? 'View as chart' : 'View as table'
      }}</Button>
    </Inline>
    <TableScroll v-if="asTable" :label="label">
      <table v-bind="s.table">
        <thead>
          <tr>
            <th v-bind="s.th" scope="col">Time (UTC)</th>
            <th v-for="serie in series" :key="serie.name" v-bind="s.th" scope="col">
              {{ serie.name }}
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.time">
            <td v-bind="s.td">{{ row.time.slice(0, 16).replace('T', ' ') }}</td>
            <td v-for="(value, index) in row.values" :key="index" v-bind="s.number">
              {{ format(value) }}
            </td>
          </tr>
        </tbody>
      </table>
    </TableScroll>
    <VChart
      v-else
      :option="option"
      autoresize
      role="img"
      :aria-label="label"
      :style="{ height: `${height ?? 240}px` }"
    />
  </div>
</template>
