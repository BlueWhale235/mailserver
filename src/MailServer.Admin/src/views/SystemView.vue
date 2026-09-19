<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '../components/PageHeader.vue'
import { usePermission } from '../composables/usePermission'

const { can } = usePermission()

interface SettingsItem {
  title: string
  description: string
  icon: string
  to: string
  permission?: string
}

const items: SettingsItem[] = [
  { title: '管理员与权限', description: 'RBAC 与域级授权', icon: 'mdi-account-key-outline', to: '/system/admins', permission: 'admin.account.read' },
  { title: 'API Key', description: '外部系统访问凭据', icon: 'mdi-key-chain', to: '/system/api-keys', permission: 'api-key.read' },
  { title: '计划任务', description: '维护、清理与证书续期', icon: 'mdi-calendar-clock-outline', to: '/system/tasks', permission: 'task.read' },
  { title: '审计日志', description: '关键操作与安全事件', icon: 'mdi-file-document-check-outline', to: '/system/audit', permission: 'audit.read' },
  { title: '插件', description: '插件顺序、状态与配置', icon: 'mdi-puzzle-outline', to: '/system/plugins', permission: 'settings.read' },
  { title: '消息模板', description: '退信与系统通知模板', icon: 'mdi-file-edit-outline', to: '/system/message-templates', permission: 'settings.read' },
  { title: '全局设置', description: 'SMTP、IMAP、存储与可观测性', icon: 'mdi-tune-variant', to: '/system/settings', permission: 'settings.read' },
  { title: '回收站', description: '恢复或永久删除对象', icon: 'mdi-delete-restore', to: '/system/recycle-bin', permission: 'task.read' },
]

const visibleItems = computed(() => items.filter(item => can(item.permission)))
</script>

<template>
  <PageHeader title="系统" description="管理员、集成、维护任务、审计与全局运行参数" />
  <div class="open-list settings-list">
    <article
      v-for="item in visibleItems"
      :key="item.title"
    >
      <span class="module-icon"><v-icon>{{ item.icon }}</v-icon></span>
      <div><h2>{{ item.title }}</h2><p>{{ item.description }}</p></div>
      <v-btn icon="mdi-chevron-right" variant="text" :to="item.to" :aria-label="`打开${item.title}`" />
    </article>
  </div>
</template>
