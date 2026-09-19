<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '../lib/api'
import { useAuthStore } from '../stores/auth'
const auth = useAuthStore()
const open = ref(false)
const busy = ref(false)
const error = ref('')
const data = ref<{ directMxAttentionDomains: number; directMxUncheckedDomains: number; expiringCertificates: number; failedDeliveries: number } | null>(null)
const alerts = computed(() => data.value ? [
  { count: data.value.directMxAttentionDomains, title: '个域 Direct MX 检查失败', to: '/domains' },
  { count: data.value.directMxUncheckedDomains, title: '个域尚未完成检查', to: '/domains' },
  { count: data.value.expiringCertificates, title: '张证书将在 30 天内到期', to: '/domains' },
  { count: data.value.failedDeliveries, title: '封邮件投递失败', to: '/delivery' },
].filter(a => a.count > 0) : [])
async function load() {
  if (busy.value) return
  busy.value = true; error.value = ''; data.value = null
  try { data.value = await api('/overview') }
  catch (e) { error.value = e instanceof Error ? e.message : '告警加载失败' }
  finally { busy.value = false }
}
function show(value: boolean) { open.value = value; if (value) void load() }
</script>
<template>
  <v-menu v-if="auth.can('dashboard.read')" :model-value="open" :close-on-content-click="false" @update:model-value="show">
    <template #activator="{ props }"><v-btn v-bind="props" icon="mdi-bell-outline" variant="text" aria-label="通知"/></template>
    <v-card width="400" title="待处理告警">
      <v-card-text>
        <v-progress-linear v-if="busy" indeterminate aria-label="正在加载告警"/>
        <v-alert v-else-if="error" type="error" variant="tonal">{{ error }}</v-alert>
        <v-list v-else-if="alerts.length"><v-list-item v-for="alert in alerts" :key="alert.title" :title="`${alert.count} ${alert.title}`" :to="alert.to" append-icon="mdi-chevron-right" @click="open = false"/></v-list>
        <p v-else-if="data">当前概览没有待处理告警。</p>
      </v-card-text>
      <v-card-actions><v-btn :loading="busy" @click="load">刷新告警</v-btn><v-spacer/><v-btn @click="open = false">关闭</v-btn></v-card-actions>
    </v-card>
  </v-menu>
</template>
