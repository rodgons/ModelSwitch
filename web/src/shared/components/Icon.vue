<script setup lang="ts">
/**
 * The dashboard's icons: one outline set drawn on a 24 px grid with a 1.5 px stroke, in currentColor (design-system.md section 11).
 * An icon next to text is decorative, so it is aria-hidden; an icon alone needs a label on its control.
 */
export type IconName =
  'overview' | 'requests' | 'providers' | 'routing' | 'tenants' | 'evaluations' | 'back'

withDefaults(defineProps<{ name: IconName; size?: 14 | 16 | 18 }>(), { size: 16 })

const paths: Record<IconName, string[]> = {
  overview: ['M4 20h16', 'M6.5 16v-5', 'M11.5 16V5', 'M16.5 16v-8'],
  requests: ['M3 12h4l3-7 4 14 3-7h4'],
  providers: [
    'M4.5 4h15a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Z',
    'M4.5 14h15a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1Z',
    'M7 7h.01',
    'M7 17h.01',
  ],
  routing: ['M6 4v16', 'M6 9c0 4 3 6 7 6h5', 'M15 12l3 3-3 3', 'M6 4h.01'],
  tenants: [
    'M4 20V5a1 1 0 0 1 1-1h8a1 1 0 0 1 1 1v15',
    'M14 9h5a1 1 0 0 1 1 1v10',
    'M3 20h18',
    'M7.5 8h3',
    'M7.5 12h3',
    'M7.5 16h3',
  ],
  evaluations: [
    'M9 3h6',
    'M10 3v6.5L4.6 18.4A1.75 1.75 0 0 0 6.1 21h11.8a1.75 1.75 0 0 0 1.5-2.6L14 9.5V3',
    'M7 15h10',
  ],
  back: ['M14.5 6l-6 6 6 6'],
}
</script>

<template>
  <svg
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.5"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <!-- non-scaling: the stroke is 1.5 px on screen at every size, not 1.5 grid units. -->
    <path v-for="d in paths[name]" :key="d" :d="d" vector-effect="non-scaling-stroke" />
  </svg>
</template>
