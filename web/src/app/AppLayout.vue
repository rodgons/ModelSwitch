<script setup lang="ts">
import { useQueryClient } from '@tanstack/vue-query'
import * as stylex from '@stylexjs/stylex'
import { useRouter } from 'vue-router'
import Button from '@/shared/components/Button.vue'
import SegmentedControl from '@/shared/components/SegmentedControl.vue'
import { shell } from '@/shared/layout/AppShell.styles'
import AppShell from '@/shared/layout/AppShell.vue'
import Inline from '@/shared/layout/Inline.vue'
import { useSession } from './session'
import { theme, type ThemeChoice } from './theme'

const session = useSession()
const account = stylex.props(shell.account)
const router = useRouter()
const queries = useQueryClient()
const links = [
  { to: '/', label: 'Overview', short: 'Ov' },
  { to: '/requests', label: 'Requests', short: 'Rq' },
  { to: '/providers', label: 'Providers', short: 'Pr' },
  { to: '/routing', label: 'Routing', short: 'Ro' },
  { to: '/tenants', label: 'Tenants', short: 'Te' },
]
const themes: { value: ThemeChoice; label: string }[] = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

async function signOut() {
  await session.signOut()
  queries.clear()
  await router.push('/sign-in')
}
</script>

<template>
  <AppShell :links="links">
    <template #topbar>
      <span />
      <Inline :gap="12" :wrap="false">
        <SegmentedControl v-model="theme" label="Theme" :options="themes" />
        <span v-bind="account" :title="session.administrator?.email"
          >{{ session.administrator?.email }} ({{ session.role }})</span
        >
        <Button variant="ghost" size="sm" @click="signOut">Sign out</Button>
      </Inline>
    </template>
    <RouterView />
  </AppShell>
</template>
