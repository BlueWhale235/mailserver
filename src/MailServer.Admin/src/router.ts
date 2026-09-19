import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import AppShell from './layouts/AppShell.vue'
import { enablePreviewMode, isPreviewMode } from './lib/mockApi'

const router = createRouter({
  history: createWebHistory('/admin/'),
  routes: [
    { path: '/login', name: 'login', component: () => import('./views/LoginView.vue'), meta: { public: true } },
    {
      path: '/', component: AppShell, children: [
        { path: '', name: 'overview', component: () => import('./views/OverviewView.vue') },
        { path: 'domains', name: 'domains', component: () => import('./views/DomainsView.vue') },
        { path: 'domains/new', name: 'domain-create', component: () => import('./views/CreateDomainView.vue') },
        { path: 'domains/:id/:tab?/:section?', name: 'domain-detail', component: () => import('./views/DomainDetailView.vue') },
        { path: 'delivery', name: 'delivery', component: () => import('./views/DeliveryView.vue') },
        { path: 'security', name: 'security', component: () => import('./views/SecurityView.vue') },
        { path: 'security/quarantine', name: 'quarantine', component: () => import('./views/QuarantineView.vue') },
        { path: 'cluster', name: 'cluster', component: () => import('./views/ClusterView.vue') },
        { path: 'system', name: 'system', component: () => import('./views/SystemView.vue') },
        { path: 'system/admins', name: 'system-admins', component: () => import('./views/system/AdminsView.vue') },
        { path: 'system/api-keys', name: 'system-api-keys', component: () => import('./views/system/ApiKeysView.vue') },
        { path: 'system/tasks', name: 'system-tasks', component: () => import('./views/system/TasksView.vue') },
        { path: 'system/audit', name: 'system-audit', component: () => import('./views/system/AuditView.vue') },
        { path: 'system/plugins', name: 'system-plugins', component: () => import('./views/system/PluginsView.vue') },
        { path: 'system/message-templates', name: 'system-templates', component: () => import('./views/system/MessageTemplatesView.vue') },
        { path: 'system/settings', name: 'system-settings', component: () => import('./views/system/SystemSettingsView.vue') },
        { path: 'system/recycle-bin', name: 'system-recycle', component: () => import('./views/system/RecycleBinView.vue') },
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

router.beforeEach(async to => {
  if (import.meta.env.DEV && (to.query.preview === '1' || isPreviewMode())) {
    enablePreviewMode()
  }
  const auth = useAuthStore()
  await auth.initialize()
  if (!to.meta.public && !auth.session) return { name: 'login', query: { redirect: to.fullPath } }
  if (to.name === 'login' && auth.session && !isPreviewMode()) return { name: 'overview' }
})

export default router
