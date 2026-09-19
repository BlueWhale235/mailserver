<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, api, jsonBody } from '../lib/api'
import PageHeader from '../components/PageHeader.vue'
import { usePermission } from '../composables/usePermission'

const router = useRouter()
const { can } = usePermission()

const form = ref({ name: '', dkimSelector: '', dkimPrivateKeyPath: '' })
const saving = ref(false)
const error = ref('')
const fieldErrors = ref<Record<string, string>>({})

const domainNameValid = computed(() => {
  const value = form.value.name.trim()
  return value.length > 0 && /^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/i.test(value) && value.includes('.')
})

function validate() {
  fieldErrors.value = {}
  let valid = true
  if (!form.value.name.trim()) {
    fieldErrors.value.name = '请输入域名'
    valid = false
  } else if (!/^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/i.test(form.value.name.trim()) || !form.value.name.trim().includes('.')) {
    fieldErrors.value.name = '请输入有效的域名，例如 example.com'
    valid = false
  }
  if (form.value.dkimSelector.trim() && !/^[a-z0-9_-]+$/i.test(form.value.dkimSelector.trim())) {
    fieldErrors.value.dkimSelector = 'DKIM Selector 只能包含字母、数字、下划线与连字符'
    valid = false
  }
  return valid
}

async function submit() {
  if (!validate()) return
  saving.value = true
  error.value = ''
  try {
    const created = await api<{ id: string }>('/domains', {
      method: 'POST',
      body: jsonBody({
        name: form.value.name.trim(),
        dkimSelector: form.value.dkimSelector.trim() || null,
        dkimPrivateKeyPath: form.value.dkimPrivateKeyPath.trim() || null,
      }),
    })
    await router.replace(`/domains/${created.id}`)
  } catch (cause) {
    error.value = cause instanceof ApiError ? cause.message : '创建域失败'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="breadcrumb">
    <router-link to="/domains">域与邮箱</router-link>
    <v-icon size="16">mdi-chevron-right</v-icon>
    <span>添加邮件域</span>
  </div>

  <PageHeader title="添加邮件域" description="创建新的邮件域并配置基础的 Direct MX 投递参数">
    <v-btn variant="outlined" prepend-icon="mdi-arrow-left" to="/domains">返回列表</v-btn>
  </PageHeader>

  <v-alert v-if="error" type="error" variant="tonal" closable class="mb-4" @click:close="error = ''">{{ error }}</v-alert>

  <v-card flat class="panel create-form-card">
    <div class="panel-head">
      <div><h2>基础信息</h2><p>域名创建后即可进行 DNS 检查与邮箱管理</p></div>
    </div>
    <div class="create-form-body">
      <v-form @submit.prevent="submit">
        <v-text-field
          v-model="form.name"
          label="域名"
          placeholder="example.com"
          :error-messages="fieldErrors.name"
          :disabled="!can('domain.manage')"
          required
          autofocus
        />

        <v-text-field
          v-model="form.dkimSelector"
          label="DKIM Selector（可选）"
          placeholder="mail"
          :error-messages="fieldErrors.dkimSelector"
          :disabled="!can('domain.manage')"
          hint="用于生成 DKIM DNS 记录，默认使用系统选择器"
          persistent-hint
        />

        <v-text-field
          v-model="form.dkimPrivateKeyPath"
          label="DKIM 私钥路径（可选）"
          placeholder="security-assets/dkim/example.com.pem"
          :disabled="!can('domain.manage')"
          hint="路径相对于服务器工作目录，留空则由系统自动生成"
          persistent-hint
        />

        <div class="create-form-actions">
          <v-spacer />
          <v-btn variant="outlined" to="/domains">取消</v-btn>
          <v-btn
            color="primary"
            type="submit"
            prepend-icon="mdi-plus"
            :loading="saving"
            :disabled="!can('domain.manage') || !domainNameValid"
          >创建域名</v-btn>
        </div>
      </v-form>
    </div>
  </v-card>
</template>

<style scoped>
.create-form-card { max-width: 760px; }

.create-form-body {
  padding: 6px 22px 22px;
}

.create-form-body :deep(.v-input) { margin-bottom: 16px; }

.create-form-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 8px;
  padding-top: 18px;
  border-top: 1px solid var(--border);
}
</style>