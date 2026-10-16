import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import { whenUnauthenticated } from '@/api/client'
import { useSession } from './session'

// frontend.md section 2. Pages are loaded on demand; every page except sign-in needs an Administrator session.
const routes: RouteRecordRaw[] = [
  {
    path: '/sign-in',
    name: 'sign-in',
    component: () => import('@/features/admin/pages/SignInPage.vue'),
    meta: { public: true },
  },
  {
    path: '/',
    component: () => import('./AppLayout.vue'),
    children: [
      {
        path: '',
        name: 'overview',
        component: () => import('@/features/overview/pages/OverviewPage.vue'),
      },
      {
        path: 'requests',
        name: 'requests',
        component: () => import('@/features/requests/pages/RequestExplorerPage.vue'),
      },
      {
        path: 'requests/:id',
        name: 'request',
        component: () => import('@/features/requests/pages/RequestDetailPage.vue'),
        props: true,
      },
      {
        path: 'providers',
        name: 'providers',
        component: () => import('@/features/providers/pages/ProvidersPage.vue'),
      },
      {
        path: 'providers/:id/models',
        name: 'provider-models',
        component: () => import('@/features/providers/pages/ProviderModelsPage.vue'),
        props: true,
      },
      {
        path: 'routing',
        name: 'routing',
        component: () => import('@/features/routing/pages/RoutingPage.vue'),
      },
      {
        path: 'tenants',
        name: 'tenants',
        component: () => import('@/features/tenants/pages/TenantsPage.vue'),
      },
      {
        path: 'tenants/:id',
        name: 'tenant',
        component: () => import('@/features/tenants/pages/TenantDetailPage.vue'),
        props: true,
      },
      {
        path: 'evaluations',
        name: 'evaluations',
        component: () => import('@/features/evaluations/pages/EvaluationsPage.vue'),
      },
      {
        path: 'evaluations/datasets/:id',
        name: 'dataset',
        component: () => import('@/features/evaluations/pages/DatasetDetailPage.vue'),
        props: true,
      },
      {
        path: 'evaluations/datasets/:id/versions/:version',
        name: 'dataset-version',
        component: () => import('@/features/evaluations/pages/DatasetVersionPage.vue'),
        props: true,
      },
      {
        path: 'evaluations/prompts/:id',
        name: 'prompt',
        component: () => import('@/features/evaluations/pages/PromptDetailPage.vue'),
        props: true,
      },
    ],
  },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

export function createAppRouter() {
  const router = createRouter({ history: createWebHistory(), routes })

  router.beforeEach(async (to) => {
    const session = useSession()
    if (!session.checked) await session.load()
    if (!to.meta.public && !session.administrator)
      return { name: 'sign-in', query: { redirect: to.fullPath } }
    if (to.name === 'sign-in' && session.administrator) return { name: 'overview' }
    return true
  })

  whenUnauthenticated(() => {
    useSession().forget()
    if (router.currentRoute.value.name !== 'sign-in')
      void router.push({ name: 'sign-in', query: { redirect: router.currentRoute.value.fullPath } })
  })

  return router
}
