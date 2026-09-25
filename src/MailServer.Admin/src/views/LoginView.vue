<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LoginIllustration from '../components/LoginIllustration.vue'
import { isPreviewMode } from '../lib/mockApi'
import { firstAccessiblePath } from '../lib/pageAccess'
import { useAuthStore } from '../stores/auth'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const previewMode = isPreviewMode()
const userName = ref('')
const password = ref('')
const remember = ref(false)
const showPassword = ref(false)
const loading = ref(false)
const error = ref('')
const sessionExpired = ref(route.query.expired === '1')
const previewCredentialsApplied = ref(false)
const form = ref<{ validate: () => Promise<{ valid: boolean }> } | null>(null)
const requiredAccount = (value: string) => !!value.trim() || '请输入管理员账号'
const requiredPassword = (value: string) => !!value || '请输入密码'

function applyPreviewCredentials() {
  userName.value = 'preview-admin'
  password.value = 'preview-password'
  error.value = ''
  sessionExpired.value = false
  previewCredentialsApplied.value = true
}

async function submit() {
  if (loading.value) return
  error.value = ''
  sessionExpired.value = false

  if (!(await form.value?.validate())?.valid) return

  loading.value = true
  try {
    await auth.login(userName.value.trim(), password.value, remember.value)
    const redirect = route.query.redirect
    const safeRedirect = typeof redirect === 'string'
      && redirect.startsWith('/')
      && !redirect.startsWith('//')
      && !redirect.startsWith('/login')
      ? redirect
      : firstAccessiblePath(auth)
    await router.replace(safeRedirect)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : '登录失败，请检查账号和密码'
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <main class="login-page">
    <section class="login-visual" aria-hidden="true">
      <div class="login-brand">
        <span class="login-brand__mark">
          <svg width="21" height="21" viewBox="0 0 24 24" fill="none">
            <path d="M4 6.5h16v11H4z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" />
            <path d="m5 8 7 5 7-5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </span>
        <span>MailServer</span>
      </div>
      <div class="login-visual__art"><LoginIllustration /></div>
      <p class="login-visual__note">专业邮件管理 · Direct MX</p>
    </section>

    <section class="login-workspace" aria-labelledby="login-title">
      <div class="login-panel">
        <div class="login-mobile-brand">
          <span class="login-brand__mark" aria-hidden="true"><v-icon>mdi-email-outline</v-icon></span>
          <span>MailServer</span>
        </div>

        <h1 id="login-title">登录控制台</h1>
        <p class="login-subtitle">使用管理员账号继续</p>

        <div v-if="previewMode" class="login-preview" role="status">
          <span>预览环境</span>
          <button type="button" :disabled="previewCredentialsApplied" @click="applyPreviewCredentials">
            {{ previewCredentialsApplied ? '演示账号已填入' : '使用演示账号' }}
          </button>
        </div>

        <v-form ref="form" class="login-form" :aria-busy="loading" @submit.prevent="submit">
          <v-alert v-if="error" role="alert" type="error" variant="tonal" density="compact" class="login-notice">
            {{ error }}
          </v-alert>
          <v-alert v-if="sessionExpired && !error" role="status" type="warning" variant="tonal" density="compact" class="login-notice">
            登录会话已过期，请重新登录后继续。
          </v-alert>

          <label for="administrator-account">管理员账号</label>
          <v-text-field
            id="administrator-account"
            :model-value="userName"
            placeholder="请输入管理员账号"
            autocomplete="username"
            variant="outlined"
            density="comfortable"
            color="primary"
            hide-details="auto"
            :rules="[requiredAccount]"
            :readonly="loading"
            required
            autofocus
            @update:model-value="value => { userName = String(value ?? ''); error = ''; sessionExpired = false }"
          />

          <label for="administrator-password">密码</label>
          <v-text-field
            id="administrator-password"
            :model-value="password"
            :type="showPassword ? 'text' : 'password'"
            placeholder="请输入密码"
            autocomplete="current-password"
            variant="outlined"
            density="comfortable"
            color="primary"
            hide-details="auto"
            :rules="[requiredPassword]"
            :readonly="loading"
            required
            @update:model-value="value => { password = String(value ?? ''); error = ''; sessionExpired = false }"
          >
            <template #append-inner>
              <v-btn
                :icon="showPassword ? 'mdi-eye-off-outline' : 'mdi-eye-outline'"
                :aria-label="showPassword ? '隐藏密码' : '显示密码'"
                :aria-pressed="showPassword"
                variant="text"
                size="small"
                type="button"
                @click="showPassword = !showPassword"
              />
            </template>
          </v-text-field>

          <div class="login-form__options">
            <v-checkbox
              v-model="remember"
              label="保持登录"
              color="primary"
              :disabled="loading"
              density="compact"
              hide-details
            />
            <span>最长 8 小时后需重新验证</span>
          </div>

          <v-btn type="submit" block :loading="loading" :disabled="loading" class="login-submit">
            登录控制台
          </v-btn>
          <p class="login-security-note">仅限授权管理员访问</p>
        </v-form>
      </div>
    </section>
  </main>
</template>
