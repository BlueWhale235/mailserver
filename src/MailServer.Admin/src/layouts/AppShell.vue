<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { isPreviewMode } from '../lib/mockApi'
import { api, jsonBody } from '../lib/api'
import ConsoleAlerts from '../components/ConsoleAlerts.vue'
import { canAccessPage, getPageDefinition, primaryPages, searchablePages } from '../lib/pageAccess'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const logoutBusy = ref(false)
const logoutError = ref('')
const selectedPage = ref<string | null>(null)
const passwordOpen = ref(false)
const passwordBusy = ref(false)
const passwordError = ref('')
const passwordNotice = ref('')
const passwordDraft = ref({ currentPassword: '', newPassword: '', confirm: '' })
const passwordValid = computed(() => passwordDraft.value.newPassword.length >= 12 && passwordDraft.value.newPassword === passwordDraft.value.confirm)
function openPassword() {
  passwordDraft.value = { currentPassword: '', newPassword: '', confirm: '' }
  passwordError.value = ''; passwordNotice.value = ''; passwordOpen.value = true
}
async function changePassword() {
  if (!passwordValid.value || passwordBusy.value) return
  passwordBusy.value = true; passwordError.value = ''
  try {
    await api('/admins/me/password', { method: 'POST', body: jsonBody({ currentPassword: passwordDraft.value.currentPassword, newPassword: passwordDraft.value.newPassword }) })
    passwordNotice.value = '密码已更新，请在下次登录时使用新密码。'
    passwordDraft.value = { currentPassword: '', newPassword: '', confirm: '' }
  } catch (e) { passwordError.value = e instanceof Error ? e.message : '密码修改失败' }
  finally { passwordBusy.value = false }
}
const pageOptions = computed(() => searchablePages
  .filter(page => canAccessPage(page, auth) && (page.routeName !== 'account-domains' || !auth.canAnyDomain('domain.read')))
  .map(page => ({ title: page.title, value: page.path })))
async function openPage(path: string | null) {
  if (path) await router.push(path)
  selectedPage.value = null
}

const items = computed(() => primaryPages
  .filter(page => canAccessPage(page, auth) && (page.routeName !== 'account-domains' || !auth.canAnyDomain('domain.read')))
  .map(page => ({ title: page.title, icon: page.icon, to: page.path })))

const active = computed(() => (route.path === '/' ? '/' : `/${route.path.split('/')[1]}`))
const currentTitle = computed(() => getPageDefinition(route.name)?.title
  ?? items.value.find(item => item.to === active.value)?.title
  ?? (route.name === 'forbidden' ? '访问受限' : '管理中心'))
const drawer = ref(true)
const now = ref(new Date())
const timer = globalThis.setInterval(() => { now.value = new Date() }, 60_000)
onUnmounted(() => globalThis.clearInterval(timer))

async function logout() {
  if (logoutBusy.value) return
  logoutBusy.value = true; logoutError.value = ''
  try { await auth.logout(); await router.push('/login') }
  catch (e) { logoutError.value = e instanceof Error ? e.message : '退出失败，请重试' }
  finally { logoutBusy.value = false }
}
</script>

<template>
    <v-navigation-drawer v-model="drawer" permanent width="248" class="app-nav">
      <div class="brand">
        <div class="brand-mark"><v-icon>mdi-email-fast-outline</v-icon></div>
        <div><b>MailServer</b><small>Direct MX 控制台</small></div>
      </div>

      <div class="nav-label">工作台</div>
      <v-list nav density="comfortable">
        <v-list-item
          v-for="item in items"
          :key="item.to"
          :active="active === item.to"
          :prepend-icon="item.icon"
          :title="item.title"
          :to="item.to"
          rounded="lg"
        />
      </v-list>

      <template #append>
        <div class="nav-info">
          <v-icon size="18">mdi-information-outline</v-icon>
          <div><b>{{ isPreviewMode() ? '演示环境' : '邮件管理控制台' }}</b><small>{{ isPreviewMode() ? '当前展示预览数据' : '服务运行状态请查看概览' }}</small></div>
        </div>
        <v-list nav density="comfortable">
          <v-list-item
            prepend-icon="mdi-account-circle-outline"
            :title="auth.session?.userName || '管理员'"
            subtitle="系统管理员"
          >
            <template #append>
              <v-tooltip text="修改密码" location="top">
                <template #activator="{ props: tooltipProps }"><v-btn v-bind="tooltipProps" icon="mdi-lock-reset" size="small" variant="text" aria-label="修改密码" @click="openPassword" /></template>
              </v-tooltip>
              <v-btn icon="mdi-logout" size="small" variant="text" aria-label="退出" :loading="logoutBusy" @click="logout" />
            </template>
          </v-list-item>
        </v-list>
      </template>
    </v-navigation-drawer>

    <v-app-bar flat height="64" class="topbar">
      <div class="topbar-title">
        <v-icon size="18" class="mr-2 text-medium-emphasis">mdi-view-grid-outline</v-icon>
        <span>{{ currentTitle }}</span>
      </div>
      <v-spacer />
      <v-autocomplete
        v-model="selectedPage"
        :items="pageOptions"
        @update:model-value="openPage"
        class="topbar-search"
        density="compact"
        variant="solo"
        flat
        hide-details
        prepend-inner-icon="mdi-magnify"
        placeholder="查找功能页面…"
        no-data-text="没有匹配的功能页面"
        single-line
        aria-label="查找功能页面"
      />
      <ConsoleAlerts />
      <v-divider vertical inset class="mx-3" />
      <span class="top-time">
        {{ now.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai' }) }}
      </span>
    </v-app-bar>

    <v-main>
      <main class="page-container">
        <v-alert v-if="logoutError" type="error" variant="tonal" class="mb-4">{{ logoutError }}</v-alert>
        <router-view />
      </main>
    </v-main>

    <v-dialog v-model="passwordOpen" max-width="560" :persistent="passwordBusy">
      <v-card title="修改登录密码">
        <v-card-text>
          <v-alert v-if="passwordError" type="error" variant="tonal" class="mb-4">{{ passwordError }}</v-alert>
          <v-alert v-if="passwordNotice" type="success" variant="tonal" class="mb-4">{{ passwordNotice }}</v-alert>
          <v-text-field v-model="passwordDraft.currentPassword" type="password" label="当前密码" autocomplete="current-password" />
          <v-text-field v-model="passwordDraft.newPassword" type="password" label="新密码" autocomplete="new-password" hint="至少 12 个字符" persistent-hint />
          <v-text-field v-model="passwordDraft.confirm" type="password" label="确认新密码" autocomplete="new-password" :error-messages="passwordDraft.confirm && passwordDraft.newPassword !== passwordDraft.confirm ? '两次输入的密码不一致' : ''" />
        </v-card-text>
        <v-card-actions><v-spacer /><v-btn :disabled="passwordBusy" @click="passwordOpen = false">关闭</v-btn><v-btn color="primary" :disabled="!passwordValid || !passwordDraft.currentPassword" :loading="passwordBusy" @click="changePassword">保存新密码</v-btn></v-card-actions>
      </v-card>
    </v-dialog>
</template>

<style scoped>
.topbar-title {
  display: flex;
  align-items: center;
  margin-left: 8px;
  font-size: 15px;
  font-weight: 600;
  color: var(--text);
}

.topbar-search {
  max-width: 260px;
  margin-right: 8px;
}

.topbar-search :deep(.v-field) {
  border-radius: 10px;
  background: var(--bg-app) !important;
}
</style>
