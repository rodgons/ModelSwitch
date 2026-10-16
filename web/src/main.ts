import * as stylex from '@stylexjs/stylex'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import { createApp } from 'vue'
import { ApiError } from '@/api/client'
import './app/global.css'
import App from './App.vue'
import { createAppRouter } from './app/router'
import { applyTheme } from './app/theme'
import { base } from './app/base.styles'

if (import.meta.env.DEV) void import('virtual:stylex:runtime')

document.body.classList.add(...(stylex.props(base.body).className ?? '').split(' ').filter(Boolean))
applyTheme()

const queries = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      // A 4xx will not get better by retrying; network and 5xx errors get one more try.
      retry: (failures, error) =>
        failures < 1 && !(error instanceof ApiError && error.status < 500),
    },
  },
})

createApp(App)
  .use(createPinia())
  .use(createAppRouter())
  .use(VueQueryPlugin, { queryClient: queries })
  .use(PrimeVue, { unstyled: true })
  .mount('#app')
