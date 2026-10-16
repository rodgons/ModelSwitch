<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import { shell } from './AppShell.styles'

/** Navigation and the scrolling main area. The topbar slot holds the time range, theme and Administrator menu. */
defineProps<{ links: { to: string; label: string; short: string }[] }>()
const open = ref(false)
const route = useRoute()
watch(
  () => route.fullPath,
  () => (open.value = false),
)
const s = {
  root: stylex.props(shell.root),
  sidebar: stylex.props(shell.sidebar),
  brand: stylex.props(shell.brand),
  brandFull: stylex.props(shell.brandFull),
  brandShort: stylex.props(shell.brandShort),
  nav: stylex.props(shell.nav),
  short: stylex.props(shell.navShort),
  label: stylex.props(shell.navLabel),
  topbar: stylex.props(shell.topbar),
  menu: stylex.props(shell.menuButton),
  main: stylex.props(shell.main),
  sheet: stylex.props(shell.sheet),
  skip: stylex.props(shell.skip),
}
const linkAttrs = (active: boolean) => stylex.props(shell.navLink, active && shell.navActive)
const isActive = (to: string) => (to === '/' ? route.path === '/' : route.path.startsWith(to))
</script>

<template>
  <div v-bind="s.root">
    <a href="#main" v-bind="s.skip">Skip to content</a>
    <nav v-bind="s.sidebar" aria-label="Main">
      <RouterLink to="/" v-bind="s.brand"
        ><span v-bind="s.brandFull">ModelSwitch</span
        ><span v-bind="s.brandShort" aria-hidden="true">MS</span></RouterLink
      >
      <ul v-bind="s.nav">
        <li v-for="link in links" :key="link.to">
          <RouterLink
            :to="link.to"
            v-bind="linkAttrs(isActive(link.to))"
            :aria-current="isActive(link.to) ? 'page' : undefined"
            :title="link.label"
          >
            <span v-bind="s.short" aria-hidden="true">{{ link.short }}</span>
            <span v-bind="s.label">{{ link.label }}</span>
          </RouterLink>
        </li>
      </ul>
    </nav>
    <header v-bind="s.topbar">
      <span v-bind="s.menu"
        ><Button
          variant="ghost"
          :aria-expanded="open"
          aria-controls="menu-sheet"
          @click="open = !open"
          >Menu</Button
        ></span
      >
      <slot name="topbar" />
    </header>
    <div v-if="open" id="menu-sheet" v-bind="s.sheet">
      <nav aria-label="Main">
        <ul v-bind="s.nav">
          <li v-for="link in links" :key="link.to">
            <RouterLink :to="link.to" v-bind="linkAttrs(isActive(link.to))">{{
              link.label
            }}</RouterLink>
          </li>
        </ul>
      </nav>
    </div>
    <main id="main" v-bind="s.main" tabindex="-1"><slot /></main>
  </div>
</template>
