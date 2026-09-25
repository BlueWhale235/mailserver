<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import LoginTopology from '../components/LoginTopology.vue'
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
    <section class="login-intro" aria-labelledby="login-product-heading">
      <header class="login-wordmark" aria-label="MailServer 邮件基础设施控制台">
        <span class="login-wordmark__mark" aria-hidden="true">
          <v-icon>mdi-server-network-outline</v-icon>
        </span>
        <span class="login-wordmark__copy">
          <strong>MailServer</strong>
          <small>邮件基础设施控制台</small>
        </span>
      </header>

      <div class="login-intro__content">
        <div class="login-intro__heading">
          <h1 id="login-product-heading">稳定、安全、可控的 Direct MX</h1>
          <p>从 DNS 解析到安全投递，构建可靠的企业邮件基础设施。</p>
        </div>

        <LoginTopology />

        <section class="login-trust" aria-labelledby="login-trust-title">
          <header class="login-trust__header">
            <div>
              <h2 id="login-trust-title">控制台安全基线</h2>
              <p>登录会话与管理操作采用统一安全策略</p>
            </div>
            <span class="login-trust__state"><i aria-hidden="true" />安全策略已启用</span>
          </header>
          <ul class="login-trust__items">
            <li>
              <span class="login-trust__icon"><v-icon aria-hidden="true">mdi-server-network-outline</v-icon></span>
              <span><strong>Direct MX</strong><small>直接投递到远端服务器</small></span>
            </li>
            <li>
              <span class="login-trust__icon"><v-icon aria-hidden="true">mdi-lock-outline</v-icon></span>
              <span><strong>TLS 1.2 / 1.3</strong><small>传输加密与证书验证</small></span>
            </li>
            <li>
              <span class="login-trust__icon"><v-icon aria-hidden="true">mdi-file-document-check-outline</v-icon></span>
              <span><strong>审计已启用</strong><small>关键管理操作可追溯</small></span>
            </li>
          </ul>
        </section>
      </div>

      <footer class="login-intro__footer">
        <span><strong>MailServer 2.0</strong> · Direct MX</span>
        <span>本地部署 · 数据自主</span>
      </footer>
    </section>

    <section class="login-workspace" aria-labelledby="login-title">
      <header class="login-workspace__header">
        <div v-if="previewMode" class="login-preview" role="status">
          <span><v-icon aria-hidden="true">mdi-flask-outline</v-icon>预览环境</span>
          <i aria-hidden="true" />
          <button type="button" :disabled="previewCredentialsApplied" @click="applyPreviewCredentials">
            {{ previewCredentialsApplied ? '演示账号已填入' : '填入演示账号' }}
          </button>
        </div>
      </header>

      <div class="login-panel">
        <h2 id="login-title">欢迎回来</h2>
        <p class="login-subtitle">登录后管理域、投递、安全策略与运行状态</p>

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
            placeholder="name@example.com"
            autocomplete="username"
            variant="outlined"
            density="comfortable"
            color="primary"
            :rules="[requiredAccount]"
            :readonly="loading"
            required
            autofocus
            prepend-inner-icon="mdi-account-outline"
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
            :rules="[requiredPassword]"
            :readonly="loading"
            required
            prepend-inner-icon="mdi-lock-outline"
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

          <v-btn
            type="submit"
            color="primary"
            block
            height="54"
            :loading="loading"
            :disabled="loading"
            class="login-submit"
            append-icon="mdi-arrow-right"
          >
            登录控制台
          </v-btn>

          <p class="login-security-note">
            <v-icon aria-hidden="true">mdi-shield-check-outline</v-icon>
            仅限授权管理员访问
          </p>
        </v-form>
      </div>

      <footer class="login-workspace__footer">
        <span>MailServer 2.0</span>
        <i aria-hidden="true" />
        <span>本地部署 · 数据自主</span>
      </footer>
    </section>
  </main>
</template>
