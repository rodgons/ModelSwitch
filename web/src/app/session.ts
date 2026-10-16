import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api, ApiError, data, setAntiforgeryToken, type Schemas } from '@/api/client'

export type Role = 'owner' | 'operator' | 'viewer'
const rank: Record<Role, number> = { viewer: 0, operator: 1, owner: 2 }

/**
 * The signed-in Administrator (session state only: server data lives in TanStack Query). The antiforgery token bound to the session
 * is handed to the API client for every state-changing call.
 */
export const useSession = defineStore('session', () => {
  const administrator = ref<Schemas['AdministratorSummary'] | null>(null)
  const checked = ref(false)

  function accept(response: Schemas['SessionResponse']) {
    administrator.value = response.administrator
    setAntiforgeryToken(response.antiforgeryToken)
  }

  /** Reads the current session once; a 401 just means nobody is signed in. */
  async function load() {
    try {
      accept(data(await api.GET('/admin/session')))
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401) throw error
      administrator.value = null
    } finally {
      checked.value = true
    }
  }

  async function signIn(email: string, password: string) {
    setAntiforgeryToken(data(await api.GET('/admin/antiforgery-token')).token)
    accept(data(await api.POST('/admin/session', { body: { email, password } })))
  }

  async function signOut() {
    try {
      await api.DELETE('/admin/session')
    } finally {
      forget()
    }
  }

  function forget() {
    administrator.value = null
    setAntiforgeryToken(null)
  }

  const role = computed(() => (administrator.value?.role ?? 'viewer') as Role)
  const can = (required: Role) => administrator.value !== null && rank[role.value] >= rank[required]

  return { administrator, checked, role, can, load, signIn, signOut, forget }
})
