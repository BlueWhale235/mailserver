export type AccessScope = 'system' | 'any-domain' | 'route-domain'

export interface AccessRequirement {
  permission: string
  scope?: AccessScope
}

export interface AdminPageDefinition {
  routeName: string
  path: string
  title: string
  icon: string
  description?: string
  accessAny: AccessRequirement[]
  primary?: boolean
  searchable?: boolean
  system?: boolean
}

export interface PermissionChecker {
  can(permission: string, domainId?: string): boolean
  canAnyDomain(permission: string): boolean
}

const systemAccess = (permission: string): AccessRequirement => ({ permission, scope: 'system' })
const anyDomainAccess = (permission: string): AccessRequirement => ({ permission, scope: 'any-domain' })
const routeDomainAccess = (permission: string): AccessRequirement => ({ permission, scope: 'route-domain' })

export const adminPages: AdminPageDefinition[] = [
  { routeName: 'overview', path: '/', title: '概览', icon: 'mdi-view-dashboard-outline', accessAny: [systemAccess('dashboard.read')], primary: true, searchable: true },
  { routeName: 'domains', path: '/domains', title: '域与邮箱', icon: 'mdi-at', accessAny: [anyDomainAccess('domain.read')], primary: true, searchable: true },
  { routeName: 'domain-create', path: '/domains/new', title: '添加域', icon: 'mdi-at-plus', accessAny: [systemAccess('domain.manage')] },
  { routeName: 'domain-detail', path: '/domains/:id', title: '域详情', icon: 'mdi-at', accessAny: [routeDomainAccess('domain.read')] },
  { routeName: 'delivery', path: '/delivery', title: '投递', icon: 'mdi-send-outline', accessAny: [systemAccess('queue.read')], primary: true, searchable: true },
  {
    routeName: 'security', path: '/security', title: '邮件安全', icon: 'mdi-shield-check-outline',
    accessAny: [systemAccess('firewall.read'), anyDomainAccess('domain.quarantine.read')], primary: true, searchable: true,
  },
  { routeName: 'quarantine', path: '/security/quarantine', title: '隔离区', icon: 'mdi-email-lock-outline', accessAny: [anyDomainAccess('domain.quarantine.read')], searchable: true },
  {
    routeName: 'system', path: '/system', title: '系统', icon: 'mdi-cog-outline', primary: true,
    accessAny: [
      systemAccess('admin.account.read'), systemAccess('api-key.read'), systemAccess('task.read'),
      systemAccess('audit.read'), systemAccess('settings.read'),
    ],
  },
  { routeName: 'system-admins', path: '/system/admins', title: '管理员与权限', description: 'RBAC 与域级授权', icon: 'mdi-account-key-outline', accessAny: [systemAccess('admin.account.read')], searchable: true, system: true },
  { routeName: 'system-api-keys', path: '/system/api-keys', title: 'API Key', description: '外部系统访问凭据', icon: 'mdi-key-chain', accessAny: [systemAccess('api-key.read')], searchable: true, system: true },
  { routeName: 'system-tasks', path: '/system/tasks', title: '计划任务', description: '回收清理、存储检查与配额校验', icon: 'mdi-calendar-clock-outline', accessAny: [systemAccess('task.read')], searchable: true, system: true },
  { routeName: 'system-audit', path: '/system/audit', title: '审计日志', description: '关键操作与安全事件', icon: 'mdi-file-document-check-outline', accessAny: [systemAccess('audit.read')], searchable: true, system: true },
  { routeName: 'system-plugins', path: '/system/plugins', title: '插件', description: '插件顺序、状态与配置', icon: 'mdi-puzzle-outline', accessAny: [systemAccess('settings.read')], searchable: true, system: true },
  { routeName: 'system-templates', path: '/system/message-templates', title: '消息模板', description: '退信与系统通知模板', icon: 'mdi-file-edit-outline', accessAny: [systemAccess('settings.read')], searchable: true, system: true },
  { routeName: 'system-settings', path: '/system/settings', title: '全局设置', description: '投递、SMTP、邮箱初始化与证书策略', icon: 'mdi-tune-variant', accessAny: [systemAccess('settings.read')], searchable: true, system: true },
  { routeName: 'system-recycle', path: '/system/recycle-bin', title: '回收站', description: '恢复或永久删除对象', icon: 'mdi-delete-restore', accessAny: [systemAccess('task.read')], searchable: true, system: true },
]

export const primaryPages = adminPages.filter(page => page.primary)
export const searchablePages = adminPages.filter(page => page.searchable)
export const systemPages = adminPages.filter(page => page.system)

export function getPageDefinition(routeName: unknown) {
  if (typeof routeName !== 'string') return undefined
  return adminPages.find(page => page.routeName === routeName)
}

export function canAccessPage(page: AdminPageDefinition, checker: PermissionChecker, routeDomainId?: string) {
  return page.accessAny.some(requirement => {
    if (requirement.scope === 'any-domain') return checker.canAnyDomain(requirement.permission)
    if (requirement.scope === 'route-domain') return Boolean(routeDomainId) && checker.can(requirement.permission, routeDomainId)
    return checker.can(requirement.permission)
  })
}

export function firstAccessiblePath(checker: PermissionChecker) {
  return adminPages.find(page => page.searchable && !page.path.includes(':') && canAccessPage(page, checker))?.path ?? '/forbidden'
}
