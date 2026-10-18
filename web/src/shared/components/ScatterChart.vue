<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { ScatterChart as EScatter } from 'echarts/charts'
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

use([EScatter, GridComponent, TooltipComponent, LegendComponent, CanvasRenderer])

/** Labelled points on two value axes, one series per point so the legend names each; with a data-table alternative (frontend.md section 3). */
const props = defineProps<{
  label: string
  points: { label: string; x: number; y: number }[]
  xName: string
  yName: string
  formatX: (value: number) => string
  formatY: (value: number) => string
  xMax?: number
  yMax?: number
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
// Points on the same spot share one label that names them all ("1, 2"); the later ones draw no label of their own.
const labels = computed(() =>
  props.points.map((p, index) => {
    const first = props.points.findIndex((q) => q.x === p.x && q.y === p.y)
    if (first !== index) return null
    return props.points
      .map((q, j) => (q.x === p.x && q.y === p.y ? String(j + 1) : null))
      .filter(Boolean)
      .join(', ')
  }),
)
const option = computed(() => {
  const palette = dark.value ? chartPalette.dark : chartPalette.light
  const axis = (name: string, format: (v: number) => string, gap: number, max?: number) => ({
    type: 'value',
    name,
    nameLocation: 'middle',
    nameGap: gap,
    nameTextStyle: { color: palette.axis },
    min: 0,
    max,
    axisLine: { lineStyle: { color: palette.grid } },
    axisLabel: { color: palette.axis, formatter: (value: number) => format(value) },
    splitLine: { lineStyle: { color: palette.grid } },
  })
  const base = chartBase(palette)
  return {
    ...base,
    color: palette.series,
    animation: false,
    grid: { left: 24, right: 32, top: 40, bottom: 24, containLabel: true },
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
      trigger: 'item',
      formatter: (p: { seriesName: string; value: [number, number] }) =>
        `${p.seriesName}<br>${props.yName}: ${props.formatY(p.value[1])}<br>${props.xName}: ${props.formatX(p.value[0])}`,
    },
    // The y name clears the widest tick label (cost and quality figures run to 5 characters and more).
    xAxis: axis(props.xName, props.formatX, 32, props.xMax),
    yAxis: axis(props.yName, props.formatY, 48, props.yMax),
    // Each point is numbered on the plot, with a ring in the surface color, so two configurations on the same spot both show.
    series: props.points.map((p, index) => ({
      name: p.label,
      type: 'scatter',
      symbolSize: 12,
      itemStyle: { borderColor: palette.surface, borderWidth: 2 },
      label: {
        show: labels.value[index] !== null,
        formatter: labels.value[index] ?? '',
        position: 'right',
        color: palette.text,
        fontFamily: base.textStyle.fontFamily,
        fontSize: 12,
      },
      data: [[p.x, p.y]],
    })),
  }
})
const s = {
  frame: stylex.props(chart.frame),
  toggle: stylex.props(chart.toggle),
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
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
            <th v-bind="s.th" scope="col">Point</th>
            <th v-bind="s.thNumber" scope="col">{{ yName }}</th>
            <th v-bind="s.thNumber" scope="col">{{ xName }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="point in points" :key="point.label">
            <td v-bind="s.td">{{ point.label }}</td>
            <td v-bind="s.number">{{ formatY(point.y) }}</td>
            <td v-bind="s.number">{{ formatX(point.x) }}</td>
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
      :style="{ height: `${height ?? 280}px` }"
    />
  </div>
</template>
