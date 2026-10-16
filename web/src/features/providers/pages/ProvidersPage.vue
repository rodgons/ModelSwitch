<script setup lang="ts">
import * as stylex from '@stylexjs/stylex'
import { useMutation } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { api, data, type Schemas } from '@/api/client'
import { useSession } from '@/app/session'
import Alert from '@/shared/components/Alert.vue'
import Badge from '@/shared/components/Badge.vue'
import Button from '@/shared/components/Button.vue'
import { link, table, typo } from '@/shared/components/components.styles'
import QueryView from '@/shared/components/QueryView.vue'
import StatusBadge from '@/shared/components/StatusBadge.vue'
import { formatDuration, formatPercent, formatRelative, formatTokens } from '@/shared/format'
import Container from '@/shared/layout/Container.vue'
import Inline from '@/shared/layout/Inline.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Section from '@/shared/layout/Section.vue'
import Stack from '@/shared/layout/Stack.vue'
import TableScroll from '@/shared/layout/TableScroll.vue'
import ProviderFormDialog from '../components/ProviderFormDialog.vue'
import { useProviderHealth, useProviders } from '../queries'

const session = useSession()
const providers = useProviders()
const health = useProviderHealth()
const editing = ref<Schemas['ProviderView'] | null>(null)
const formOpen = ref(false)
const checks = ref<Record<string, Schemas['ConnectivityView'] | Error>>({})

function open(provider: Schemas['ProviderView'] | null) {
  editing.value = provider
  formOpen.value = true
}

const check = useMutation({
  mutationFn: async (providerId: string) =>
    data(
      await api.POST('/admin/providers/{providerId}/connectivity-check', {
        params: { path: { providerId } },
      }),
    ),
  onSuccess: (result, providerId) => (checks.value = { ...checks.value, [providerId]: result }),
  onError: (error, providerId) => (checks.value = { ...checks.value, [providerId]: error }),
})

const checkText = (result: Schemas['ConnectivityView'] | Error | undefined) => {
  if (!result) return null
  if (result instanceof Error) return { tone: 'danger' as const, text: result.message }
  return result.reachable
    ? { tone: 'success' as const, text: `Reachable in ${formatDuration(result.latencyMs)}` }
    : {
        tone: 'danger' as const,
        text: `Unreachable: ${[result.failure?.class, result.failure?.code, result.failure?.httpStatus].filter(Boolean).join(' · ')}`,
      }
}

const healthRows = computed(() => health.data.value?.items ?? [])
const s = {
  table: stylex.props(table.table),
  th: stylex.props(table.th),
  thNumber: stylex.props(table.th, table.number),
  td: stylex.props(table.td),
  number: stylex.props(table.td, table.number),
  mono: stylex.props(typo.mono),
  caption: stylex.props(typo.caption),
  section: stylex.props(link.section, link.focus),
}
</script>

<template>
  <Container>
    <PageHeader
      title="Providers"
      lede="The upstream inference services the gateway can call, and how healthy each Provider Model is right now."
    >
      <template #actions
        ><Button v-if="session.can('owner')" variant="primary" @click="open(null)"
          >Add Provider</Button
        ></template
      >
    </PageHeader>
    <Stack :gap="48">
      <Section title="Providers" surface flush>
        <QueryView
          :data="providers.data.value"
          :is-pending="providers.isPending.value"
          :error="providers.error.value"
          :refetch="providers.refetch"
          :is-empty="(page) => page.items.length === 0"
          empty-text="No Providers yet. Add one to start routing requests."
        >
          <template #empty
            ><Button v-if="session.can('owner')" variant="primary" @click="open(null)"
              >Add Provider</Button
            ></template
          >
          <template #default="{ data: page }">
            <TableScroll label="Providers">
              <table v-bind="s.table">
                <thead>
                  <tr>
                    <th v-bind="s.th" scope="col">Name</th>
                    <th v-bind="s.th" scope="col">Adapter</th>
                    <th v-bind="s.th" scope="col">Endpoint</th>
                    <th v-bind="s.th" scope="col">Credential</th>
                    <th v-bind="s.th" scope="col">State</th>
                    <th v-bind="s.th" scope="col"><span v-bind="s.caption">Actions</span></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="provider in page.items" :key="provider.id">
                    <td v-bind="s.td">
                      <span v-bind="s.mono">{{ provider.name }}</span>
                    </td>
                    <td v-bind="s.td">
                      {{ provider.adapterType === 'ollama' ? 'Ollama' : 'OpenAI-compatible' }}
                    </td>
                    <td v-bind="s.td">
                      <span v-bind="s.mono">{{ provider.endpoint }}</span>
                    </td>
                    <td v-bind="s.td">
                      <span v-if="provider.credential.configured"
                        >{{
                          provider.credential.kind === 'db'
                            ? 'Stored secret'
                            : 'Environment variable'
                        }}
                        · configured</span
                      >
                      <span v-else v-bind="s.caption">None</span>
                    </td>
                    <td v-bind="s.td">
                      <Badge :tone="provider.enabled ? 'success' : 'neutral'">{{
                        provider.enabled ? 'Enabled' : 'Disabled'
                      }}</Badge>
                    </td>
                    <td v-bind="s.td">
                      <Stack :gap="4">
                        <Inline :gap="4" :wrap="false">
                          <Button size="sm" variant="ghost" :to="`/providers/${provider.id}/models`"
                            >Models</Button
                          >
                          <template v-if="session.can('owner')">
                            <Button size="sm" variant="ghost" @click="open(provider)">Edit</Button>
                            <Button
                              size="sm"
                              variant="ghost"
                              :loading="
                                check.isPending.value && check.variables.value === provider.id
                              "
                              @click="check.mutate(provider.id)"
                              >Test connection</Button
                            >
                          </template>
                        </Inline>
                        <Badge
                          v-if="checkText(checks[provider.id])"
                          :tone="checkText(checks[provider.id])!.tone"
                          role="status"
                          >{{ checkText(checks[provider.id])!.text }}</Badge
                        >
                      </Stack>
                    </td>
                  </tr>
                </tbody>
              </table>
            </TableScroll>
          </template>
        </QueryView>
      </Section>
      <Section title="Provider Health">
        <Alert tone="info"
          >Circuits are per api instance. The state shown is the worst any instance reported in the
          last minute; error rate and latency cover the last 15 minutes.</Alert
        >
        <QueryView
          :data="health.data.value"
          :is-pending="health.isPending.value"
          :error="health.error.value"
          :refetch="health.refetch"
          :is-empty="(view) => view.items.length === 0"
          empty-text="No Provider Model yet. Add models to a Provider to see their health."
        >
          <TableScroll label="Provider Health">
            <table v-bind="s.table">
              <thead>
                <tr>
                  <th v-bind="s.th" scope="col">Provider Model</th>
                  <th v-bind="s.th" scope="col">Circuit</th>
                  <th v-bind="s.th" scope="col">Instances</th>
                  <th v-bind="s.thNumber" scope="col">Attempts</th>
                  <th v-bind="s.thNumber" scope="col">Error rate</th>
                  <th v-bind="s.thNumber" scope="col">Latency p50 / p95</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="item in healthRows" :key="item.model.id">
                  <td v-bind="s.td">
                    <Inline :gap="6">
                      <span v-bind="s.mono"
                        >{{ item.model.providerName }}/{{ item.model.name }}</span
                      >
                      <Badge v-if="!item.enabled" tone="neutral">Disabled</Badge>
                    </Inline>
                  </td>
                  <td v-bind="s.td"><StatusBadge :status="item.state" /></td>
                  <td v-bind="s.td">
                    <span v-if="!item.instances.length" v-bind="s.caption">No report yet</span>
                    <Stack v-else :gap="2">
                      <span
                        v-for="instance in item.instances"
                        :key="instance.instanceId"
                        v-bind="s.caption"
                      >
                        {{ instance.instanceId }}: {{ instance.state
                        }}{{
                          instance.stale ? `, stale (${formatRelative(instance.capturedAt)})` : ''
                        }}
                      </span>
                    </Stack>
                  </td>
                  <td v-bind="s.number">{{ formatTokens(item.recent.attempts) }}</td>
                  <td v-bind="s.number">{{ formatPercent(item.recent.errorRate) }}</td>
                  <td v-bind="s.number">
                    {{ formatDuration(item.recent.latencyP50Ms) }} /
                    {{ formatDuration(item.recent.latencyP95Ms) }}
                  </td>
                </tr>
              </tbody>
            </table>
          </TableScroll>
        </QueryView>
      </Section>
    </Stack>
    <ProviderFormDialog v-model:visible="formOpen" :provider="editing" />
  </Container>
</template>
