<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { RouterLink } from 'vue-router'
import Alert from '@/shared/components/Alert.vue'
import { link } from '@/shared/components/components.styles'
import { useRun } from '../runQueries'

/**
 * On a request an Evaluation Run made: "candidate (or judge) for Case X of run Y", linking to the Case in its Comparison and to the
 * run (evaluation.md section 3).
 */
const props = defineProps<{ runId: string; externalId: string; role: string }>()
const run = useRun(() => props.runId)
const s = stylex.props(link.inline, link.focus)
</script>

<template>
  <Alert tone="info">
    <span>
      {{ role === 'judge' ? 'Judge' : 'Candidate' }} for Case
      <RouterLink
        v-if="run.data.value"
        :to="{ path: `/comparisons/${run.data.value.comparisonId}`, query: { case: externalId } }"
        v-bind="s"
        ><code>{{ externalId }}</code></RouterLink
      ><code v-else>{{ externalId }}</code>
      of run
      <RouterLink :to="`/evaluations/runs/${runId}`" v-bind="s">{{
        run.data.value?.reproducibility.target.label ?? runId
      }}</RouterLink
      ><template v-if="run.data.value"> in Comparison {{ run.data.value.comparisonName }}</template
      >.
    </span>
  </Alert>
</template>
