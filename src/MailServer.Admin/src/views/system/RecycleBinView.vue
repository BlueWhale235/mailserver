<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'
import { usePermission } from '../../composables/usePermission'
import { api } from '../../lib/api'

type ResourceType = 'Domain' | 'Account' | 'Mailbox' | 'MailboxMessage' | 'ContentFile'
type QueueStatus = 'Pending' | 'Failed'

interface RecycleBinItem {
  queueId: string
  resourceType: ResourceType
  resourceId: string | null
  displayName: string
  estimatedBytes: number
  deletedAt: string
  purgeAfter: string
  status: QueueStatus
}

const resourceTypes: Array<{ title: string; value: '' | ResourceType }> = [
  { title: '全部类型', value: '' },
  { title: '域', value: 'Domain' },
  { title: '账号', value: 'Account' },
  { title: '邮箱文件夹', value: 'Mailbox' },
  { title: '邮件', value: 'MailboxMessage' },
  { title: '内容文件', value: 'ContentFile' },
]
const statusTypes: Array<{ title: string; value: '' | QueueStatus }> = [
  { title: '全部状态', value: '' },
  { title: '等待清理', value: 'Pending' },
  { title: '清理失败', value: 'Failed' },
]

const { can } = usePermission()
const items = ref<RecycleBinItem[]>([])
const loading = ref(false)
const actionLoading = ref(false)
const error = ref('')
const notice = ref('')
const search = ref<string | null>('')
const resourceType = ref<'' | ResourceType>('')
const status = ref<'' | QueueStatus>('')
const target = ref<RecycleBinItem | null>(null)
const action = ref<'restore' | 'purge'>('restore')
const confirmation = ref('')
const dialogError = ref('')

const filteredItems = computed(() => {
  const needle = search.value?.trim().toLowerCase() ?? ''
  return items.value.filter(item => {
    const haystack = `${item.displayName} ${item.queueId} ${item.resourceId ?? ''}`.toLowerCase()
    return (!needle || haystack.includes(needle))
      && (!resourceType.value || item.resourceType === resourceType.value)
      && (!status.value || item.status === status.value)
  })
})
const totalBytes = computed(() => items.value.reduce((sum, item) => sum + item.estimatedBytes, 0))
const failedCount = computed(() => items.value.filter(item => item.status === 'Failed').length)
const urgentCount = computed(() => items.value.filter(item => new Date(item.purgeAfter).getTime() - Date.now() <= 72 * 60 * 60 * 1000).length)
const activeFilterCount = computed(() => Number(Boolean(search.value?.trim())) + Number(Boolean(resourceType.value)) + Number(Boolean(status.value)))
const canRestoreTarget = computed(() => target.value ? isRestorable(target.value) : false)
const canExecute = computed(() => {
  if (!target.value || actionLoading.value) return false
  return action.value === 'restore' ? canRestoreTarget.value : confirmation.value === target.value.displayName
})

async function load() {
  loading.value = true
  error.value = ''
  try {
    items.value = await api<RecycleBinItem[]>('/recycle-bin')
  } catch (cause) {
    items.value = []
    error.value = cause instanceof Error ? cause.message : '回收站加载失败'
  } finally {
    loading.value = false
  }
}

function resetFilters() {
  search.value = ''
  resourceType.value = ''
  status.value = ''
}

function openAction(item: RecycleBinItem, operation: 'restore' | 'purge') {
  if (operation === 'restore' && !isRestorable(item)) return
  target.value = item
  action.value = operation
  confirmation.value = ''
  dialogError.value = ''
}

function closeDialog() {
  if (actionLoading.value) return
  target.value = null
  confirmation.value = ''
  dialogError.value = ''
}

async function execute() {
  if (!target.value || !canExecute.value) return
  actionLoading.value = true
  dialogError.value = ''
  const currentTarget = target.value
  try {
    await api(`/recycle-bin/${currentTarget.queueId}${action.value === 'restore' ? '/restore' : ''}`, {
      method: action.value === 'restore' ? 'POST' : 'DELETE',
    })
    notice.value = action.value === 'restore'
      ? `“${currentTarget.displayName}”已恢复。`
      : `“${currentTarget.displayName}”已永久清理。`
    target.value = null
    await load()
  } catch (cause) {
    dialogError.value = cause instanceof Error ? cause.message : '操作失败，请稍后重试'
  } finally {
    actionLoading.value = false
  }
}

function isRestorable(item: RecycleBinItem) {
  return item.resourceType !== 'ContentFile' && Boolean(item.resourceId)
}

function typeView(value: ResourceType) {
  const views: Record<ResourceType, { label: string; icon: string }> = {
    Domain: { label: '域', icon: 'mdi-web' },
    Account: { label: '账号', icon: 'mdi-account-outline' },
    Mailbox: { label: '邮箱文件夹', icon: 'mdi-folder-outline' },
    MailboxMessage: { label: '邮件', icon: 'mdi-email-outline' },
    ContentFile: { label: '内容文件', icon: 'mdi-file-outline' },
  }
  return views[value]
}

function statusView(value: QueueStatus) {
  return value === 'Failed'
    ? { label: '清理失败', tone: 'danger' as const }
    : { label: '等待清理', tone: 'neutral' as const }
}

function formatDate(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

function formatBytes(value: number) {
  if (value <= 0) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  const scaled = value / 1024 ** index
  return `${scaled.toFixed(scaled >= 10 || index === 0 ? 0 : 1)} ${units[index]}`
}

function purgeWindow(value: string) {
  const delta = new Date(value).getTime() - Date.now()
  if (delta <= 0) return { label: '已到清理时间', urgent: true }
  const hours = Math.ceil(delta / 3_600_000)
  if (hours < 24) return { label: `${hours} 小时后`, urgent: true }
  const days = Math.ceil(hours / 24)
  return { label: `${days} 天后`, urgent: days <= 3 }
}

function shortId(value: string) {
  return value.slice(0, 8)
}

onMounted(load)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>回收站</span>
  </div>

  <PageHeader title="回收站" description="审阅软删除对象，在保留期内恢复，或确认后永久清理">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="load">刷新</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>

  <section class="recycle-summary" aria-label="回收站摘要">
    <div><span>待处理对象</span><strong>{{ items.length }}</strong><small>等待恢复或自动清理</small></div>
    <div><span>预计可释放</span><strong>{{ formatBytes(totalBytes) }}</strong><small>最终释放量以清理结果为准</small></div>
    <div><span>清理失败</span><strong :class="{ danger: failedCount }">{{ failedCount }}</strong><small>需要检查任务与存储状态</small></div>
    <div><span>即将或已经到期</span><strong :class="{ warning: urgentCount }">{{ urgentCount }}</strong><small>72 小时内进入自动清理</small></div>
  </section>

  <v-alert type="warning" variant="tonal" density="compact" class="recycle-warning">
    到达计划清理时间后，维护任务会永久删除对象。永久清理无法撤销，关联邮件内容也可能被一并释放。
  </v-alert>

  <v-card flat class="panel recycle-panel">
    <div class="recycle-toolbar">
      <div class="recycle-filters">
        <v-text-field
          v-model="search"
          label="搜索回收站"
          placeholder="对象名称或 ID"
          prepend-inner-icon="mdi-magnify"
          clearable
          hide-details
        />
        <v-select v-model="resourceType" :items="resourceTypes" label="资源类型" hide-details />
        <v-select v-model="status" :items="statusTypes" label="清理状态" hide-details />
      </div>
      <div class="recycle-filter-meta">
        <span>{{ activeFilterCount ? `已应用 ${activeFilterCount} 项筛选` : `共 ${items.length} 个对象` }}</span>
        <v-btn variant="text" size="small" :disabled="!activeFilterCount" @click="resetFilters">重置筛选</v-btn>
      </div>
    </div>

    <v-table class="recycle-table">
      <thead>
        <tr><th>对象</th><th>类型</th><th>状态</th><th>预计占用</th><th>删除时间</th><th>计划清理</th><th class="text-end">操作</th></tr>
      </thead>
      <tbody>
        <tr v-if="loading"><td colspan="7"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!filteredItems.length">
          <td colspan="7" class="recycle-empty">
            <v-icon size="34">mdi-delete-clock-outline</v-icon>
            <b>{{ items.length ? '没有符合筛选条件的对象' : '回收站为空' }}</b>
            <span>{{ items.length ? '调整搜索或筛选条件后重试' : '被软删除的域、账号、邮箱和邮件会显示在这里' }}</span>
          </td>
        </tr>
        <tr v-for="item in filteredItems" v-else :key="item.queueId">
          <td class="object-cell">
            <b>{{ item.displayName }}</b>
            <small>队列 {{ shortId(item.queueId) }}<template v-if="item.resourceId"> · 资源 {{ shortId(item.resourceId) }}</template></small>
          </td>
          <td><span class="resource-type"><v-icon size="17">{{ typeView(item.resourceType).icon }}</v-icon>{{ typeView(item.resourceType).label }}</span></td>
          <td><StatusPill v-bind="statusView(item.status)" /></td>
          <td class="bytes-cell">{{ formatBytes(item.estimatedBytes) }}</td>
          <td>{{ formatDate(item.deletedAt) }}</td>
          <td>
            <span class="purge-date">{{ formatDate(item.purgeAfter) }}</span>
            <small :class="{ urgent: purgeWindow(item.purgeAfter).urgent }">{{ purgeWindow(item.purgeAfter).label }}</small>
          </td>
          <td class="action-cell">
            <template v-if="can('task.manage')">
              <v-btn
                size="small"
                variant="text"
                prepend-icon="mdi-restore"
                :disabled="loading || !isRestorable(item)"
                :title="isRestorable(item) ? `恢复 ${item.displayName}` : '内容文件没有可恢复的数据库对象'"
                :aria-label="isRestorable(item) ? `恢复 ${item.displayName}` : `${item.displayName} 不支持恢复`"
                @click="openAction(item, 'restore')"
              >恢复</v-btn>
              <v-btn
                size="small"
                color="error"
                variant="text"
                prepend-icon="mdi-delete-forever-outline"
                :disabled="loading"
                :aria-label="`永久清理 ${item.displayName}`"
                @click="openAction(item, 'purge')"
              >永久清理</v-btn>
            </template>
            <span v-else class="permission-note">仅查看</span>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>

  <v-dialog :model-value="Boolean(target)" max-width="600" :persistent="actionLoading" @update:model-value="value => { if (!value) closeDialog() }">
    <v-card v-if="target">
      <v-card-title class="dialog-title">
        <span class="dialog-icon" :class="action">
          <v-icon>{{ action === 'restore' ? 'mdi-restore' : 'mdi-delete-forever-outline' }}</v-icon>
        </span>
        <span>{{ action === 'restore' ? '恢复回收站对象' : '永久清理对象' }}</span>
      </v-card-title>
      <v-card-text>
        <v-alert v-if="dialogError" type="error" variant="tonal" density="compact" class="mb-4">{{ dialogError }}</v-alert>

        <div class="dialog-object">
          <span>{{ typeView(target.resourceType).label }}</span>
          <strong>{{ target.displayName }}</strong>
          <small>预计占用 {{ formatBytes(target.estimatedBytes) }} · 计划于 {{ formatDate(target.purgeAfter) }} 清理</small>
        </div>

        <template v-if="action === 'restore'">
          <p class="dialog-copy">恢复后，对象会重新出现在原有位置。关联对象的恢复范围由资源类型决定。</p>
          <v-alert type="info" variant="tonal" density="compact">该操作会写入审计日志。</v-alert>
        </template>
        <template v-else>
          <v-alert type="error" variant="tonal" density="compact" class="mb-4">
            此操作无法撤销，并会跳过剩余保留期。关联的数据库记录和邮件内容可能被永久释放。
          </v-alert>
          <label class="confirmation-label" for="purge-confirmation">输入对象名称以确认</label>
          <code class="confirmation-value">{{ target.displayName }}</code>
          <v-text-field
            id="purge-confirmation"
            v-model="confirmation"
            label="对象名称"
            autocomplete="off"
            :disabled="actionLoading"
            autofocus
            class="mt-3"
            @keyup.enter="execute"
          />
        </template>
      </v-card-text>
      <v-card-actions class="dialog-actions">
        <v-btn :disabled="actionLoading" @click="closeDialog">取消</v-btn>
        <v-btn
          :color="action === 'restore' ? 'primary' : 'error'"
          :prepend-icon="action === 'restore' ? 'mdi-restore' : 'mdi-delete-forever-outline'"
          :disabled="!canExecute"
          :loading="actionLoading"
          @click="execute"
        >{{ action === 'restore' ? '确认恢复' : '确认永久清理' }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.recycle-summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); margin-bottom: 16px; overflow: hidden; background: #fff; border: 1px solid var(--border); border-radius: 8px; }
.recycle-summary > div { display: grid; grid-template-columns: 1fr auto; gap: 2px 14px; min-width: 0; padding: 16px 20px; border-right: 1px solid var(--border); }
.recycle-summary > div:last-child { border-right: 0; }
.recycle-summary span { align-self: center; color: var(--text-secondary); font-size: 12px; font-weight: 650; }
.recycle-summary strong { grid-row: 1 / 3; grid-column: 2; align-self: center; color: var(--text); font-size: 23px; line-height: 1; }
.recycle-summary strong.danger { color: #bd3030; }
.recycle-summary strong.warning { color: #ad6800; }
.recycle-summary small { min-width: 0; overflow: hidden; color: var(--text-muted); font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.recycle-warning { margin-bottom: 16px; }
.recycle-panel { overflow: hidden; }
.recycle-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 16px 18px; border-bottom: 1px solid var(--border); }
.recycle-filters { display: grid; grid-template-columns: minmax(280px, 1.6fr) minmax(160px, .7fr) minmax(150px, .65fr); gap: 10px; width: min(800px, 75%); }
.recycle-filter-meta { display: flex; align-items: center; gap: 8px; color: var(--text-muted); font-size: 12px; white-space: nowrap; }
.recycle-table :deep(th) { white-space: nowrap; }
.recycle-table :deep(td) { color: var(--text-secondary); font-size: 12px; }
.object-cell { min-width: 250px; }
.object-cell b, .object-cell small, .purge-date, .purge-date + small { display: block; }
.object-cell b { max-width: 360px; overflow: hidden; color: var(--text); font-size: 13px; text-overflow: ellipsis; white-space: nowrap; }
.object-cell small { margin-top: 4px; color: var(--text-muted); font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 10px; }
.resource-type { display: inline-flex; align-items: center; gap: 7px; white-space: nowrap; }
.resource-type :deep(.v-icon) { color: #55769d; }
.bytes-cell { color: var(--text) !important; font-variant-numeric: tabular-nums; font-weight: 650; white-space: nowrap; }
.purge-date { color: var(--text-secondary); white-space: nowrap; }
.purge-date + small { margin-top: 4px; color: var(--text-muted); }
.purge-date + small.urgent { color: #b56d00; font-weight: 700; }
.action-cell { min-width: 218px; text-align: right; white-space: nowrap; }
.permission-note { color: var(--text-muted); }
.recycle-empty { height: 210px; color: var(--text-muted) !important; text-align: center; }
.recycle-empty :deep(.v-icon), .recycle-empty b, .recycle-empty span { display: block; margin: 5px auto; }
.recycle-empty b { color: var(--text-secondary); font-size: 14px; }
.dialog-title { display: flex; align-items: center; gap: 12px; padding-top: 20px; font-size: 17px; }
.dialog-icon { display: grid; width: 38px; height: 38px; place-items: center; color: #1769e0; background: #edf5ff; border-radius: 8px; }
.dialog-icon.purge { color: #bd3030; background: #fff0f0; }
.dialog-object { display: flex; flex-direction: column; gap: 4px; margin-bottom: 18px; padding: 14px 16px; background: #f7f9fc; border: 1px solid var(--border); border-radius: 8px; }
.dialog-object span { color: var(--text-muted); font-size: 11px; }
.dialog-object strong { overflow-wrap: anywhere; color: var(--text); font-size: 14px; }
.dialog-object small { color: var(--text-muted); font-size: 11px; }
.dialog-copy { margin: 0 0 16px; color: var(--text-secondary); font-size: 13px; line-height: 1.7; }
.confirmation-label { display: block; color: var(--text-secondary); font-size: 12px; font-weight: 650; }
.confirmation-value { display: block; margin-top: 6px; overflow-wrap: anywhere; color: #9c2929; font-size: 12px; }
.dialog-actions { padding: 12px 20px 18px; }
@media (max-width: 1280px) {
  .recycle-summary { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .recycle-summary > div:nth-child(2) { border-right: 0; }
  .recycle-summary > div:nth-child(-n + 2) { border-bottom: 1px solid var(--border); }
  .recycle-toolbar { align-items: stretch; flex-direction: column; }
  .recycle-filters { width: 100%; }
  .recycle-filter-meta { justify-content: space-between; }
}
@media (max-width: 720px) {
  .recycle-summary { grid-template-columns: 1fr; }
  .recycle-summary > div { border-right: 0; border-bottom: 1px solid var(--border); }
  .recycle-summary > div:nth-child(2) { border-right: 0; }
  .recycle-summary > div:nth-child(-n + 3) { border-bottom: 1px solid var(--border); }
  .recycle-summary > div:last-child { border-bottom: 0; }
  .recycle-filters { grid-template-columns: 1fr; }
  .recycle-filter-meta { align-items: flex-start; flex-direction: column; }
}
</style>
