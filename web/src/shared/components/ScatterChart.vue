<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { ScatterChart as EScatter } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, ref } from 'vue'
import VChart from 'vue-echarts'
import { theme } from '@/app/theme'
import { chartPalette } from '@/shared/design/chart'
import Inline from '@/shared/layout/Inline.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import Button from './Button.vue'
import { table } from './components.styles'

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
const option = computed(() => {
  const palette = dark.value ? chartPalette.dark : chartPalette.light
  const axis = (name: string, format: (v: number) => string, max?: number) => ({
    type: 'value',
    name,
    nameLocation: 'middle',
    nameGap: 32,
    nameTextStyle: { color: palette.axis },
    min: 0,
    max,
    axisLine: { lineStyle: { color: palette.grid } },
    axisLabel: { color: palette.axis, formatter: (value: number) => format(value) },
    splitLine: { lineStyle: { color: palette.grid } },
  })
  return {
    color: palette.series,
    animation: false,
    grid: { left: 16, right: 24, top: 40, bottom: 24, containLabel: true },
    legend: { top: 0, left: 0, textStyle: { color: palette.text } },
    tooltip: {
      trigger: 'item',
      formatter: (p: { seriesName: string; value: [number, number] }) =>
        `${p.seriesName}<br>${props.yName}: ${props.formatY(p.value[1])}<br>${props.xName}: ${props.formatX(p.value[0])}`,
    },
    xAxis: axis(props.xName, props.formatX, props.xMax),
    yAxis: axis(props.yName, props.formatY, props.yMax),
    series: props.points.map((p) => ({
      name: p.label,
      type: 'scatter',
      symbolSize: 12,
      data: [[p.x, p.y]],
    })),
  }
})
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
}
</script>

<template>
  <div>
    <Inline justify="end">
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
