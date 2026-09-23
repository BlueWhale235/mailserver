<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import PageHeader from '../components/PageHeader.vue'
import { useAuthStore } from '../stores/auth'
import { firstAccessiblePath } from '../lib/pageAccess'

const route = useRoute()
const auth = useAuthStore()
const target = computed(() => typeof route.query.target === 'string' ? route.query.target : '此页面')
const requestedPath = computed(() => {
  const value = route.query.from
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') ? value : ''
})
const safeHome = computed(() => firstAccessiblePath(auth))
</script>

<template>
  <PageHeader title="访问受限" description="当前账号没有访问该管理功能所需的权限" />
  <v-card flat class="panel forbidden-panel">
    <div class="forbidden-icon"><v-icon size="42">mdi-shield-lock-outline</v-icon></div>
    <div>
      <h2>无法打开{{ target }}</h2>
      <p>这通常表示管理员尚未为当前账号授予对应权限，或者该权限只适用于其他邮件域。</p>
      <code v-if="requestedPath">{{ requestedPath }}</code>
      <div class="forbidden-actions">
        <v-btn color="primary" prepend-icon="mdi-arrow-left" :to="safeHome">返回可访问页面</v-btn>
      </div>
    </div>
  </v-card>
</template>

<style scoped>
.forbidden-panel { display: grid; grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: 22px; max-width: 760px; padding: 30px; }
.forbidden-icon { display: grid; place-items: center; width: 72px; height: 72px; border-radius: 16px; color: #9a5e00; background: #fff4d8; }
h2 { margin: 3px 0 8px; font-size: 20px; }
p { max-width: 600px; margin: 0 0 16px; color: var(--text-secondary); line-height: 1.7; }
code { display: inline-block; max-width: 100%; overflow: hidden; padding: 7px 10px; border: 1px solid var(--border); border-radius: 6px; color: var(--text-muted); background: #f8fafc; text-overflow: ellipsis; white-space: nowrap; }
.forbidden-actions { display: flex; justify-content: flex-end; margin-top: 24px; }
@media (max-width: 640px) { .forbidden-panel { grid-template-columns: 1fr; } }
</style>
