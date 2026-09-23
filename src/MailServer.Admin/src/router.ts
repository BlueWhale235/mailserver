import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from './stores/auth'
import { setSessionExpiredHandler } from './lib/api'
import AppShell from './layouts/AppShell.vue'
import { enablePreviewMode, isPreviewMode } from './lib/mockApi'
import { canAccessPage, firstAccessiblePath, getPageDefinition } from './lib/pageAccess'

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
        { path: 'system', name: 'system', component: () => import('./views/SystemView.vue') },
        { path: 'system/admins', name: 'system-admins', component: () => import('./views/system/AdminsView.vue') },
        { path: 'system/api-keys', name: 'system-api-keys', component: () => import('./views/system/ApiKeysView.vue') },
        { path: 'system/tasks', name: 'system-tasks', component: () => import('./views/system/TasksView.vue') },
        { path: 'system/audit', name: 'system-audit', component: () => import('./views/system/AuditView.vue') },
        { path: 'system/plugins', name: 'system-plugins', component: () => import('./views/system/PluginsView.vue') },
        { path: 'system/message-templates', name: 'system-templates', component: () => import('./views/system/MessageTemplatesView.vue') },
        { path: 'system/settings', name: 'system-settings', component: () => import('./views/system/SystemSettingsView.vue') },
        { path: 'system/recycle-bin', name: 'system-recycle', component: () => import('./views/system/RecycleBinView.vue') },
        { path: 'forbidden', name: 'forbidden', component: () => import('./views/ForbiddenView.vue') },
      ]
    },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

/**
 * 任意 API 返回 401 时统一清理会话并回到登录页，保留安全的站内返回地址。
 * 并发 401 由 api.ts 去重，这里只处理一次；403 不触发本逻辑。
 */
function installSessionExpiryHandler() {
  setSessionExpiredHandler(() => {
    const auth = useAuthStore()
    const current = router.currentRoute.value
    // 启动时的会话探测也会得到 401（此时本就没有会话），不应提示“会话已过期”。
    if (!auth.session) return
    const redirect = current.name === 'login' ? undefined : current.fullPath
    auth.clearSession()
    if (current.name === 'login') return
    void router.replace({
      name: 'login',
      query: {
        expired: '1',
        ...(redirect && redirect.startsWith('/') && !redirect.startsWith('//') ? { redirect } : {}),
      },
    })
  })
}

router.beforeEach(async to => {
  if (import.meta.env.DEV && (to.query.preview === '1' || isPreviewMode())) {
    enablePreviewMode()
  }
  installSessionExpiryHandler()
  const auth = useAuthStore()
  await auth.initialize()
  if (!to.meta.public && !auth.session) return { name: 'login', query: { redirect: to.fullPath } }
  if (to.name === 'login' && auth.session && !isPreviewMode()) return firstAccessiblePath(auth)
  const page = getPageDefinition(to.name)
  const routeDomainId = typeof to.params.id === 'string' ? to.params.id : undefined
  if (page && !canAccessPage(page, auth, routeDomainId)) {
    return {
      name: 'forbidden',
      query: {
        from: to.fullPath.startsWith('/') && !to.fullPath.startsWith('//') ? to.fullPath : undefined,
        target: page.title,
      },
    }
  }
})

export default router
