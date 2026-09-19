<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { api, jsonBody } from '../lib/api'
import { usePermission } from '../composables/usePermission'

export interface SettingField { key: string; label: string; type?: 'number' | 'multiline' | 'list' | 'boolean'; min?: number; max?: number }
const props = defineProps<{ endpoint: string; title: string; fields: SettingField[] }>()
const { can } = usePermission()
const values = ref<Record<string, string | number | boolean>>({})
const baseline = ref('')
const ready = ref(false)
const busy = ref(false)
const error = ref('')
const notice = ref('')
const dirty = computed(() => ready.value && JSON.stringify(values.value) !== baseline.value)
function accept(data: Record<string, unknown>) {
  values.value = Object.fromEntries(props.fields.map(f => {
    const value = data[f.key]
    return [f.key, f.type === 'list' ? (Array.isArray(value) ? value.join('\n') : '') : typeof value === 'boolean' || typeof value === 'number' ? value : String(value ?? '')]
  }))
  baseline.value = JSON.stringify(values.value)
  ready.value = true
}
async function load() {
  busy.value = true; error.value = ''
  try { accept(await api<Record<string, unknown>>(props.endpoint)) }
  catch (e) { error.value = e instanceof Error ? e.message : '加载失败' }
  finally { busy.value = false }
}
function reset() { values.value = JSON.parse(baseline.value); notice.value = ''; error.value = '' }
async function save() {
  if (busy.value) return
  const payload: Record<string, unknown> = {}
  for (const field of props.fields) {
    const value = values.value[field.key]
    if (field.type === 'number' && (!Number.isInteger(Number(value)) || String(value).trim() === '' || Number(value) < (field.min ?? 0) || Number(value) > (field.max ?? Number.MAX_SAFE_INTEGER))) {
      error.value = `${field.label}必须是 ${field.min ?? 0} 至 ${field.max ?? '允许上限'} 之间的整数`; return
    }
    payload[field.key] = field.type === 'number' ? Number(value) : field.type === 'list' ? String(value).split('\n').map(v => v.trim()).filter(Boolean) : value
  }
  busy.value = true; error.value = ''; notice.value = ''
  try { accept(await api<Record<string, unknown>>(props.endpoint, { method: 'PUT', body: jsonBody(payload) })); notice.value = '设置已保存' }
  catch (e) { error.value = e instanceof Error ? e.message : '保存失败' }
  finally { busy.value = false }
}
onBeforeRouteLeave(() => !dirty.value || globalThis.confirm('存在未保存的设置，确定离开？'))
onMounted(load)
</script>
<template>
  <section class="panel pa-6 mb-5">
    <h2 class="text-h6 mb-5">{{ title }}</h2>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}<v-btn v-if="!ready" variant="text" @click="load">重试</v-btn></v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mb-4">{{ notice }}</v-alert>
    <v-progress-linear v-if="busy" indeterminate class="mb-4"/>
    <v-form v-if="ready" @submit.prevent="save">
      <div style="max-width: 880px">
        <template v-for="field in fields" :key="field.key">
          <v-switch v-if="field.type === 'boolean'" v-model="values[field.key]" :label="field.label" :disabled="busy || !can('settings.manage')"/>
          <v-textarea v-else-if="field.type === 'multiline' || field.type === 'list'" v-model="values[field.key]" :label="field.label" :hint="field.type === 'list' ? '每行一项' : undefined" persistent-hint rows="5" :readonly="busy || !can('settings.manage')" class="mb-6"/>
          <v-text-field v-else v-model="values[field.key]" :label="field.label" :type="field.type === 'number' ? 'number' : 'text'" :min="field.min" :max="field.max" :readonly="busy || !can('settings.manage')" class="mb-6"/>
        </template>
      </div>
      <div v-if="can('settings.manage')" class="d-flex ga-3">
        <v-btn color="primary" type="submit" :disabled="!dirty" :loading="busy">保存设置</v-btn>
        <v-btn variant="outlined" :disabled="!dirty || busy" @click="reset">撤销修改</v-btn>
      </div>
    </v-form>
  </section>
</template>
