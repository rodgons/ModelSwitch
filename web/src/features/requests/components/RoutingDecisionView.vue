<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { computed } from 'vue'
import type { Schemas } from '@/api/client'
import { typo } from '@/shared/components/components.styles'
import Badge from '@/shared/components/Badge.vue'
import Facts from '@/shared/layout/Facts.vue'
import Stack from '@/shared/layout/Stack.vue'

/** The recorded Routing Decision: the ordered eligible Candidates, the excluded ones with their reasons, and the rule matched. */
const props = defineProps<{
  decision: Schemas['RoutingDecisionView'] | null
  names: Map<string, string>
  servedBy: string | null
}>()
const value = computed(
  () =>
    props.decision ?? {
      strategy: null,
      ruleMatched: null,
      maxOutputTokens: null,
      ordered: [],
      excluded: [],
    },
)
const name = (id: string) => props.names.get(id) ?? id
const facts = computed(() => [
  { label: 'Strategy', value: value.value.strategy ?? '—' },
  {
    label: 'Rule matched',
    value: value.value.ruleMatched === null ? 'None' : `#${value.value.ruleMatched + 1}`,
  },
  { label: 'Output ceiling', value: `${value.value.maxOutputTokens ?? '—'} tokens` },
])
const s = {
  mono: stylex.props(typo.mono),
  caption: stylex.props(typo.caption),
  list: stylex.props(typo.body),
}
</script>

<template>
  <Stack :gap="16">
    <Facts :items="facts" />
    <Stack :gap="6">
      <span v-bind="s.caption">Eligible Candidates, in order</span>
      <ol v-bind="s.list">
        <li v-for="id in value.ordered" :key="id">
          <span v-bind="s.mono">{{ name(id) }}</span>
          <Badge v-if="servedBy === id" tone="success">Served</Badge>
        </li>
      </ol>
      <span v-if="!value.ordered.length" v-bind="s.caption"
        >None: routing failed before any Candidate was eligible.</span
      >
    </Stack>
    <Stack v-if="value.excluded.length" :gap="6">
      <span v-bind="s.caption">Excluded</span>
      <ul v-bind="s.list">
        <li v-for="excluded in value.excluded" :key="excluded.model">
          <span v-bind="s.mono">{{ name(excluded.model) }}</span
          >: <span v-bind="s.mono">{{ excluded.reason }}</span>
        </li>
      </ul>
    </Stack>
  </Stack>
</template>
