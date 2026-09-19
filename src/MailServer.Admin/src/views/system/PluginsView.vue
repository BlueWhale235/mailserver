<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import PageHeader from '../../components/PageHeader.vue'
import StatusPill from '../../components/StatusPill.vue'
import { ApiError, api, jsonBody } from '../../lib/api'
import { usePermission } from '../../composables/usePermission'

type SettingType = 'Text' | 'Integer' | 'Boolean' | 'Select' | 'Multiselect' | 'Password'
type SettingValue = string | number | boolean | string[] | null

interface Plugin {
  id: string
  displayName: string
  description: string
  version: string
  system: boolean
  enabled: boolean
  isHostActive: boolean
  loadError?: string
  order: number
  effectiveOrder: number
}

interface Definition {
  key: string
  label: string
  type: SettingType
  description?: string
  required?: boolean
  minimum?: number
  maximum?: number
  placeholder?: string
  options: Array<{ label: string; value: string }>
}

interface SettingRecord {
  key: string
  value: SettingValue
  hasSecret: boolean
}

interface Configuration {
  plugin: Plugin
  definitions: Definition[]
  values: SettingRecord[]
}

const { can } = usePermission()

const plugins = ref<Plugin[]>([])
const loading = ref(false)
const ordering = ref(false)
const error = ref('')
const notice = ref('')

const configOpen = ref(false)
const configTarget = ref<Plugin | null>(null)
const configuration = ref<Configuration | null>(null)
const configLoading = ref(false)
const configSaving = ref(false)
const configError = ref('')
const enabled = ref(false)
const values = ref<Record<string, SettingValue>>({})
const clearedSecrets = ref<string[]>([])
const baseline = ref('')
const discardOpen = ref(false)

const enabledCount = computed(() => plugins.value.filter(plugin => plugin.enabled).length)
const activeCount = computed(() => plugins.value.filter(plugin => plugin.isHostActive).length)
const failedCount = computed(() => plugins.value.filter(plugin => plugin.loadError).length)
const configDirty = computed(() => Boolean(configuration.value) && currentSnapshot() !== baseline.value)
const configValid = computed(() => {
  if (!configuration.value) return false
  return configuration.value.definitions.every(field => {
    const value = values.value[field.key]
    if (field.type === 'Integer') {
      const number = Number(value)
      return String(value ?? '').trim() !== '' && Number.isInteger(number)
        && number >= (field.minimum ?? Number.MIN_SAFE_INTEGER)
        && number <= (field.maximum ?? Number.MAX_SAFE_INTEGER)
    }
    if (field.required && field.type === 'Text') return Boolean(String(value ?? '').trim())
    if (field.type === 'Select') return field.options.some(option => option.value === value)
    if (field.type === 'Multiselect') return Array.isArray(value) && value.every(item => field.options.some(option => option.value === item))
    return true
  })
})

function currentSnapshot() {
  return JSON.stringify({ enabled: enabled.value, values: values.value, clearedSecrets: [...clearedSecrets.value].sort() })
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    plugins.value = await api<Plugin[]>('/plugins')
  } catch (cause) {
    plugins.value = []
    error.value = cause instanceof Error ? cause.message : '插件列表加载失败'
  } finally {
    loading.value = false
  }
}

async function openConfiguration(plugin: Plugin) {
  configOpen.value = true
  configTarget.value = plugin
  configuration.value = null
  configError.value = ''
  configLoading.value = true
  clearedSecrets.value = []
  try {
    const result = await api<Configuration>(`/plugins/${encodeURIComponent(plugin.id)}/configuration`)
    configuration.value = result
    enabled.value = result.plugin.enabled
    values.value = Object.fromEntries(result.values.map(setting => [setting.key, setting.value ?? '']))
    baseline.value = currentSnapshot()
  } catch (cause) {
    configError.value = cause instanceof Error ? cause.message : '插件配置加载失败'
  } finally {
    configLoading.value = false
  }
}

function requestCloseConfiguration() {
  if (configSaving.value) return
  if (configDirty.value) {
    discardOpen.value = true
    return
  }
  closeConfiguration()
}

function closeConfiguration() {
  configOpen.value = false
  discardOpen.value = false
  configTarget.value = null
  configuration.value = null
  values.value = {}
  clearedSecrets.value = []
  baseline.value = ''
  configError.value = ''
}

function updateValue(field: Definition, value: SettingValue) {
  values.value[field.key] = value
  if (field.type === 'Password' && String(value ?? '')) {
    clearedSecrets.value = clearedSecrets.value.filter(key => key !== field.key)
  }
}

function hasStoredSecret(key: string) {
  return Boolean(configuration.value?.values.find(setting => setting.key === key)?.hasSecret)
}

function toggleClearSecret(key: string) {
  if (clearedSecrets.value.includes(key)) {
    clearedSecrets.value = clearedSecrets.value.filter(item => item !== key)
  } else {
    clearedSecrets.value = [...clearedSecrets.value, key]
    values.value[key] = ''
  }
}

function validationMessage(field: Definition) {
  const value = values.value[field.key]
  if (field.type === 'Integer') {
    const number = Number(value)
    if (String(value ?? '').trim() === '' || !Number.isInteger(number)) return '请输入整数'
    if (number < (field.minimum ?? Number.MIN_SAFE_INTEGER) || number > (field.maximum ?? Number.MAX_SAFE_INTEGER)) {
      return `允许范围 ${field.minimum ?? '不限'} 至 ${field.maximum ?? '不限'}`
    }
  }
  if (field.required && field.type === 'Text' && !String(value ?? '').trim()) return '此项不能为空'
  return ''
}

async function saveConfiguration() {
  if (!configuration.value || !configDirty.value || !configValid.value) return
  configSaving.value = true
  configError.value = ''
  try {
    const payload: Record<string, SettingValue> = {}
    for (const field of configuration.value.definitions) {
      const value = values.value[field.key]
      if (field.type === 'Password' && !value) continue
      payload[field.key] = field.type === 'Integer' ? Number(value) : value
    }
    const pluginName = configuration.value.plugin.displayName
    await api(`/plugins/${encodeURIComponent(configuration.value.plugin.id)}/configuration`, {
      method: 'PUT',
      body: jsonBody({ enabled: enabled.value, values: payload, clearedSecrets: clearedSecrets.value }),
    })
    closeConfiguration()
    notice.value = `${pluginName} 的配置已保存`
    await load()
  } catch (cause) {
    configError.value = cause instanceof ApiError || cause instanceof Error ? cause.message : '插件配置保存失败'
  } finally {
    configSaving.value = false
  }
}

async function move(index: number, offset: number) {
  if (ordering.value || index + offset < 0 || index + offset >= plugins.value.length) return
  const ordered = [...plugins.value]
  const [item] = ordered.splice(index, 1)
  if (!item) return
  ordered.splice(index + offset, 0, item)
  ordering.value = true
  error.value = ''
  try {
    plugins.value = await api<Plugin[]>('/plugins/order', {
      method: 'PUT',
      body: jsonBody({ pluginIds: ordered.map(plugin => plugin.id) }),
    })
    notice.value = `${item.displayName} 已${offset < 0 ? '上移' : '下移'}，执行顺序已保存`
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '插件顺序保存失败'
  } finally {
    ordering.value = false
  }
}

function runtimeStatus(plugin: Plugin) {
  if (plugin.loadError) return { label: '加载失败', tone: 'danger' as const }
  if (plugin.isHostActive) return { label: '已加载', tone: 'success' as const }
  return { label: '未激活', tone: 'neutral' as const }
}

onBeforeRouteLeave(() => !configDirty.value || globalThis.confirm('插件配置尚未保存，确定离开？'))
onMounted(load)
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/system">系统</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>插件</span>
  </div>

  <PageHeader title="插件" description="管理邮件处理扩展的状态、执行顺序与运行参数">
    <v-btn variant="outlined" prepend-icon="mdi-refresh" :loading="loading" @click="load">刷新</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>
  <v-alert v-if="notice" type="success" variant="tonal" closable class="mb-4" @click:close="notice = ''">{{ notice }}</v-alert>
  <v-alert v-if="failedCount" type="warning" variant="tonal" class="mb-4">
    {{ failedCount }} 个插件未能正常加载。请查看错误并先停用有问题的插件，再检查程序集与签名。
  </v-alert>

  <div class="plugin-summary" aria-label="插件状态摘要">
    <span><b>{{ plugins.length }}</b>插件总数</span>
    <span><b>{{ enabledCount }}</b>已启用</span>
    <span><b>{{ activeCount }}</b>宿主已加载</span>
    <span :class="{ 'is-danger': failedCount }"><b>{{ failedCount }}</b>加载失败</span>
  </div>

  <v-card flat class="panel table-panel">
    <div class="panel-head">
      <div><h2>执行链</h2><p>邮件按列表顺序进入插件；调整后立即保存新的优先级</p></div>
    </div>
    <v-table>
      <thead><tr><th class="order-cell">顺序</th><th>插件</th><th>启用状态</th><th>宿主状态</th><th>版本</th><th></th></tr></thead>
      <tbody>
        <tr v-if="loading"><td colspan="6"><v-progress-linear indeterminate color="primary" /></td></tr>
        <tr v-else-if="!plugins.length"><td colspan="6" class="text-center py-10 text-medium-emphasis">{{ error ? '插件列表加载失败' : '没有发现插件' }}</td></tr>
        <tr v-for="(plugin, index) in plugins" v-else :key="plugin.id">
          <td class="order-cell"><span>{{ index + 1 }}</span></td>
          <td class="plugin-name">
            <div><b>{{ plugin.displayName }}</b><v-chip size="x-small" variant="tonal">{{ plugin.system ? '系统' : '扩展' }}</v-chip></div>
            <small>{{ plugin.description }}</small>
            <code>{{ plugin.id }}</code>
          </td>
          <td><StatusPill :label="plugin.enabled ? '已启用' : '已禁用'" :tone="plugin.enabled ? 'success' : 'neutral'" /></td>
          <td><StatusPill v-bind="runtimeStatus(plugin)" /><small v-if="plugin.loadError" class="load-error">{{ plugin.loadError }}</small></td>
          <td><code>v{{ plugin.version }}</code></td>
          <td class="actions-cell">
            <v-btn size="small" variant="text" prepend-icon="mdi-tune-variant" :aria-label="`配置 ${plugin.displayName}`" @click="openConfiguration(plugin)">配置</v-btn>
            <template v-if="can('settings.manage')">
              <v-btn size="small" variant="text" icon="mdi-arrow-up" :disabled="ordering || index === 0" :aria-label="`${plugin.displayName} 上移`" @click="move(index, -1)" />
              <v-btn size="small" variant="text" icon="mdi-arrow-down" :disabled="ordering || index === plugins.length - 1" :aria-label="`${plugin.displayName} 下移`" @click="move(index, 1)" />
            </template>
          </td>
        </tr>
      </tbody>
    </v-table>
  </v-card>

  <v-dialog v-model="configOpen" max-width="760" persistent>
    <v-card>
      <v-card-title class="dialog-title">
        <span><v-icon color="primary">mdi-puzzle-outline</v-icon> 配置插件</span>
        <v-btn icon="mdi-close" variant="text" aria-label="关闭插件配置" :disabled="configSaving" @click="requestCloseConfiguration" />
      </v-card-title>
      <v-card-text>
        <v-progress-linear v-if="configLoading" indeterminate color="primary" />
        <v-alert v-if="configError" type="error" variant="tonal" class="mb-4">{{ configError }}</v-alert>
        <template v-if="configuration">
          <div class="config-lead">
            <div><h2>{{ configuration.plugin.displayName }}</h2><p><code>{{ configuration.plugin.id }}</code> · v{{ configuration.plugin.version }}</p></div>
            <v-chip size="small" variant="tonal">{{ configuration.plugin.system ? '系统插件' : '扩展插件' }}</v-chip>
          </div>
          <v-alert v-if="configuration.plugin.loadError" type="warning" variant="tonal" density="compact" class="mb-4">
            宿主加载失败：{{ configuration.plugin.loadError }}。可以先停用插件，修复程序集后再启用。
          </v-alert>
          <section class="enable-row">
            <div><b>参与邮件处理</b><span>关闭后插件保留配置，但不参与后续邮件处理。</span></div>
            <v-switch v-model="enabled" color="primary" hide-details :disabled="configSaving || !can('settings.manage')" label="启用插件" />
          </section>

          <div v-if="configuration.definitions.length" class="settings-list">
            <div v-for="field in configuration.definitions" :key="field.key" class="setting-field">
              <template v-if="field.type === 'Boolean'">
                <div class="field-copy"><b>{{ field.label }}</b><span v-if="field.description">{{ field.description }}</span><code>{{ field.key }}</code></div>
                <v-switch :model-value="Boolean(values[field.key])" color="primary" hide-details :disabled="configSaving || !can('settings.manage')" @update:model-value="value => updateValue(field, Boolean(value))" />
              </template>
              <template v-else>
                <div class="field-heading"><label :for="`plugin-${field.key}`">{{ field.label }}<em v-if="field.required">必填</em></label><code>{{ field.key }}</code></div>
                <v-select
                  v-if="field.type === 'Select' || field.type === 'Multiselect'"
                  :id="`plugin-${field.key}`"
                  :model-value="values[field.key]"
                  :items="field.options"
                  item-title="label"
                  item-value="value"
                  :multiple="field.type === 'Multiselect'"
                  :chips="field.type === 'Multiselect'"
                  :hint="field.description"
                  persistent-hint
                  :disabled="configSaving || !can('settings.manage')"
                  @update:model-value="value => updateValue(field, value as SettingValue)"
                />
                <v-text-field
                  v-else
                  :id="`plugin-${field.key}`"
                  :model-value="values[field.key]"
                  :type="field.type === 'Password' ? 'password' : field.type === 'Integer' ? 'number' : 'text'"
                  :placeholder="field.type === 'Password' && hasStoredSecret(field.key) ? '已保存；留空保持不变' : field.placeholder"
                  :hint="field.description"
                  persistent-hint
                  :min="field.minimum"
                  :max="field.maximum"
                  :error-messages="validationMessage(field)"
                  :readonly="configSaving || !can('settings.manage')"
                  @update:model-value="value => updateValue(field, value as SettingValue)"
                />
                <div v-if="field.type === 'Password'" class="secret-state">
                  <span v-if="clearedSecrets.includes(field.key)" class="is-danger"><v-icon size="16">mdi-key-remove</v-icon> 保存后清除已存密钥</span>
                  <span v-else-if="String(values[field.key] || '')"><v-icon size="16">mdi-key-change</v-icon> 保存后替换密钥</span>
                  <span v-else-if="hasStoredSecret(field.key)"><v-icon size="16">mdi-key-check</v-icon> 已安全保存密钥</span>
                  <span v-else><v-icon size="16">mdi-key-outline</v-icon> 尚未设置密钥</span>
                  <v-btn v-if="hasStoredSecret(field.key)" size="small" variant="text" :color="clearedSecrets.includes(field.key) ? undefined : 'error'" :disabled="configSaving || !can('settings.manage')" @click="toggleClearSecret(field.key)">
                    {{ clearedSecrets.includes(field.key) ? '撤销清除' : '清除已存密钥' }}
                  </v-btn>
                </div>
              </template>
            </div>
          </div>
          <div v-else class="empty-config"><v-icon size="28">mdi-tune-variant</v-icon><b>没有额外运行参数</b><span>仍可通过上方开关启用或停用该插件。</span></div>
        </template>
      </v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" :disabled="configSaving" @click="requestCloseConfiguration">取消</v-btn><v-btn v-if="can('settings.manage')" color="primary" :loading="configSaving" :disabled="configLoading || !configDirty || !configValid" @click="saveConfiguration">保存配置</v-btn></v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="discardOpen" max-width="460" persistent>
    <v-card>
      <v-card-title>放弃未保存修改？</v-card-title>
      <v-card-text>插件状态或运行参数已经变化。放弃后，本次修改不会保存。</v-card-text>
      <v-card-actions><v-spacer /><v-btn variant="text" @click="discardOpen = false">继续编辑</v-btn><v-btn color="error" variant="tonal" @click="closeConfiguration">放弃修改</v-btn></v-card-actions>
    </v-card>
  </v-dialog>
</template>

<style scoped>
.plugin-summary { display: flex; align-items: center; margin-bottom: 16px; overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.plugin-summary span { display: flex; align-items: baseline; gap: 7px; min-width: 150px; padding: 13px 18px; border-right: 1px solid var(--border); color: var(--text-muted); font-size: 12px; }
.plugin-summary span:last-child { border-right: 0; }
.plugin-summary b { color: var(--text); font-size: 19px; }
.plugin-summary .is-danger b { color: #c92a3a; }
.panel-head { padding: 20px; border-bottom: 1px solid var(--border); }
.panel-head h2 { margin: 0 0 3px; font-size: 16px; }
.panel-head p { margin: 0; color: var(--text-muted); font-size: 12px; }
.order-cell { width: 74px; text-align: center !important; }
.order-cell span { display: inline-grid; width: 27px; height: 27px; place-items: center; border: 1px solid var(--border); border-radius: 7px; color: var(--text-secondary); background: #f8fafc; font-size: 12px; font-weight: 700; }
.plugin-name { max-width: 480px; }
.plugin-name > div { display: flex; align-items: center; gap: 8px; }
.plugin-name small, .plugin-name code, .load-error { display: block; margin-top: 3px; }
.plugin-name code { color: var(--text-muted); font-size: 11px; }
.load-error { max-width: 230px; color: #b42335; white-space: normal; }
.actions-cell { min-width: 196px; text-align: right !important; white-space: nowrap; }
.dialog-title { display: flex; align-items: center; justify-content: space-between; padding-right: 12px; }
.dialog-title > span { display: inline-flex; align-items: center; gap: 8px; }
.config-lead { display: flex; align-items: flex-start; justify-content: space-between; gap: 20px; margin-bottom: 18px; }
.config-lead h2 { margin: 0; font-size: 18px; }
.config-lead p { margin: 4px 0 0; color: var(--text-muted); font-size: 12px; }
.enable-row { display: flex; align-items: center; justify-content: space-between; gap: 24px; margin-bottom: 14px; padding: 14px 16px; border: 1px solid var(--border); border-radius: 8px; background: #f8fafc; }
.enable-row > div { display: flex; flex-direction: column; gap: 3px; }
.enable-row b { font-size: 13px; }
.enable-row span { color: var(--text-muted); font-size: 12px; }
.settings-list { overflow: hidden; border: 1px solid var(--border); border-radius: 8px; }
.setting-field { padding: 15px 16px 10px; border-bottom: 1px solid var(--border); }
.setting-field:last-child { border-bottom: 0; }
.setting-field:has(.field-copy) { display: flex; align-items: center; justify-content: space-between; gap: 24px; padding-bottom: 15px; }
.field-copy { display: flex; flex-direction: column; gap: 3px; }
.field-copy b, .field-heading label { color: var(--text); font-size: 13px; font-weight: 700; }
.field-copy span { color: var(--text-muted); font-size: 12px; }
.field-copy code, .field-heading code { color: var(--text-muted); font-size: 11px; }
.field-heading { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin-bottom: 7px; }
.field-heading em { margin-left: 6px; color: #b42335; font-size: 10px; font-style: normal; }
.secret-state { display: flex; align-items: center; justify-content: space-between; min-height: 32px; margin-top: -8px; }
.secret-state span { display: inline-flex; align-items: center; gap: 5px; color: var(--text-muted); font-size: 12px; }
.secret-state .is-danger { color: #b42335; }
.empty-config { display: flex; align-items: center; flex-direction: column; gap: 5px; padding: 30px; border: 1px dashed var(--border); border-radius: 8px; color: var(--text-muted); }
.empty-config b { color: var(--text-secondary); font-size: 13px; }
.empty-config span { font-size: 12px; }
@media (max-width: 760px) {
  .plugin-summary { align-items: stretch; flex-wrap: wrap; }
  .plugin-summary span { flex: 1 1 50%; }
  .config-lead, .enable-row { align-items: stretch; flex-direction: column; }
}
</style>
