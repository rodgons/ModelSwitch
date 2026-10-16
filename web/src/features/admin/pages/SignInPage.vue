<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import { useSession } from '@/app/session'
import Alert from '@/shared/components/Alert.vue'
import Button from '@/shared/components/Button.vue'
import Field from '@/shared/components/Field.vue'
import TextInput from '@/shared/components/TextInput.vue'
import Container from '@/shared/layout/Container.vue'
import PageHeader from '@/shared/layout/PageHeader.vue'
import Stack from '@/shared/layout/Stack.vue'

const session = useSession()
const router = useRouter()
const route = useRoute()
const email = ref('')
const password = ref('')
const pending = ref(false)
const failure = ref<string | null>(null)

async function submit() {
  pending.value = true
  failure.value = null
  try {
    await session.signIn(email.value, password.value)
    const redirect =
      typeof route.query.redirect === 'string' && route.query.redirect.startsWith('/')
        ? route.query.redirect
        : '/'
    await router.replace(redirect)
  } catch (error) {
    // Wrong, unknown, deactivated and locked accounts all answer the same (security.md section 3).
    failure.value =
      error instanceof ApiError && error.status === 401
        ? 'The email or password is not right, or the account is locked for 15 minutes after repeated failures.'
        : 'Signing in failed. Try again in a moment.'
  } finally {
    pending.value = false
  }
}
</script>

<template>
  <Container size="form">
    <PageHeader
      title="Sign in to ModelSwitch"
      lede="Administrators manage Providers, routing and Tenants here."
    />
    <form novalidate @submit.prevent="submit">
      <Stack :gap="16">
        <Alert v-if="failure" tone="danger">{{ failure }}</Alert>
        <Field v-slot="{ id }" label="Email">
          <TextInput :id="id" v-model="email" type="email" autocomplete="username" />
        </Field>
        <Field v-slot="{ id }" label="Password">
          <TextInput :id="id" v-model="password" type="password" autocomplete="current-password" />
        </Field>
        <div><Button type="submit" variant="primary" :loading="pending">Sign in</Button></div>
      </Stack>
    </form>
  </Container>
</template>
