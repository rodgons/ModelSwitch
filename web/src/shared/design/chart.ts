// Chart colors (design-system.md section 2.3: series use a categorical palette, never the status hues). Canvas charts need concrete
// colors, so this is the one place outside the token files with hex values; each pair is the light and the dark variant.
export const chartPalette = {
  light: {
    series: ['#7139c6', '#035172', '#8a5a00', '#3c3749', '#a0458a'],
    axis: '#554f63',
    grid: '#d4d0dc',
    text: '#14101e',
    // bg.raised and border.default: the tooltip is a floating surface like a menu. `surface` is what a chart is drawn on.
    tooltip: '#ffffff',
    tooltipBorder: '#d4d0dc',
    surface: '#ffffff',
  },
  dark: {
    series: ['#bd94f9', '#5abee2', '#e0b35c', '#bdbbc4', '#e48ccf'],
    axis: '#9e9ba6',
    grid: '#3d3a45',
    text: '#f2f0f4',
    tooltip: '#292631',
    tooltipBorder: '#3d3a45',
    surface: '#221f29',
  },
}

/** A canvas cannot read the StyleX font variable, so charts name the same stack as `font.sans` (type.stylex.ts). */
export const chartFont = '"Atkinson Hyperlegible Next", system-ui, sans-serif'

/** What every chart shares: the UI typeface at caption size, and a tooltip in the theme instead of ECharts' white box. */
export function chartBase(palette: (typeof chartPalette)['light']) {
  return {
    textStyle: { fontFamily: chartFont, fontSize: 12, color: palette.axis },
    tooltip: {
      backgroundColor: palette.tooltip,
      borderColor: palette.tooltipBorder,
      borderWidth: 1,
      padding: [8, 12],
      textStyle: { fontFamily: chartFont, fontSize: 12, color: palette.text },
      extraCssText: 'border-radius: 8px; box-shadow: 0 12px 32px -8px rgb(20 16 30 / .22);',
    },
  }
}
