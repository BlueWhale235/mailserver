<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '../lib/api'
import type { DirectoryPage } from '../lib/accountDirectory'
import PageHeader from '../components/PageHeader.vue'
const rows = ref<{ id: string; name: string }[]>([]), query = ref(''), page = ref(1), total = ref(0), loading = ref(false), error = ref('')
let request = 0
async function load() {
  const current = ++request
  loading.value = true; error.value = ''; rows.value = []
  try {
    const result = await api<DirectoryPage<{ id: string; name: string }>>(`/account-directories?${new URLSearchParams({ query: query.value ?? '', page: String(page.value) })}`)
    if (current !== request) return
    rows.value = result.items; total.value = result.totalCount
  } catch (cause) { if (current === request) error.value = (cause as Error).message }
  finally { if (current === request) loading.value = false }
}
watch(page, () => { void load() }, { immediate: true })
function search() { if (page.value === 1) void load(); else page.value = 1 }
</script>
<template>
  <PageHeader title="账号和别名" description="选择已授权的邮件域，管理其中的账号和别名。" />
  <v-card flat class="panel pa-6">
    <form class="domain-search" @submit.prevent="search"><v-text-field v-model="query" label="搜索邮件域" hide-details clearable /><v-btn type="submit" color="primary" :disabled="loading">搜索</v-btn></form>
    <v-alert v-if="error" type="error" variant="tonal">{{ error }}<v-btn variant="text" @click="load">重试</v-btn></v-alert>
    <v-table><thead><tr><th>邮件域</th><th class="text-right">操作</th></tr></thead><tbody><tr v-if="loading"><td colspan="2">正在加载…</td></tr><tr v-else-if="!rows.length && !error"><td colspan="2">没有符合条件的邮件域</td></tr><tr v-for="row in rows" :key="row.id"><td>{{ row.name }}</td><td class="text-right"><v-btn variant="text" :to="{ name: 'account-directory', params: { id: row.id } }">账号和别名</v-btn></td></tr></tbody></v-table>
    <v-pagination v-model="page" :length="Math.max(1, Math.ceil(total / 20))" :total-visible="5" :disabled="loading" />
  </v-card>
</template>
<style scoped>.domain-search{display:flex;align-items:center;gap:16px;max-width:720px;margin-bottom:24px}</style>
