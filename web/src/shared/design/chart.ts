// Chart colors (design-system.md section 2.3: series use a categorical palette, never the status hues). Canvas charts need concrete
// colors, so this is the one place outside the token files with hex values; each pair is the light and the dark variant.
export const chartPalette = {
  light: {
    series: ['#7139c6', '#035172', '#8a5a00', '#3c3749', '#a0458a'],
    axis: '#554f63',
    grid: '#d4d0dc',
    text: '#14101e',
  },
  dark: {
    series: ['#bd94f9', '#5abee2', '#e0b35c', '#bdbbc4', '#e48ccf'],
    axis: '#9e9ba6',
    grid: '#3d3a45',
    text: '#f2f0f4',
  },
}
