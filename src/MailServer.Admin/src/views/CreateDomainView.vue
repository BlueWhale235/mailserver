<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { ApiError, api, jsonBody } from '../lib/api'
import { domainNameError, normalizeDomainName } from '../lib/domainName'
import PageHeader from '../components/PageHeader.vue'
import { usePermission } from '../composables/usePermission'

const router = useRouter()
const { can } = usePermission()

const form = ref({ name: '', dkimSelector: '' })
const saving = ref(false)
const error = ref('')
const fieldErrors = ref<Record<string, string>>({})
const touched = ref({ name: false, dkimSelector: false })
const submitted = ref(false)

const nameError = computed(() => domainNameError(form.value.name))
const selectorError = computed(() => {
  const value = form.value.dkimSelector.trim()
  if (!value) return ''
  if (!/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/i.test(value)) return 'DKIM Selector 最多 63 个字符，只能包含字母、数字和连字符，且不能以连字符开头或结尾'
  return ''
})
const formValid = computed(() => !nameError.value && !selectorError.value)
const normalizedName = computed(() => normalizeDomainName(form.value.name))
const dirty = computed(() => !!form.value.name.trim() || !!form.value.dkimSelector.trim())

function beforeUnload(event: BeforeUnloadEvent) {
  if (submitted.value || (!dirty.value && !saving.value)) return
  event.preventDefault()
  event.returnValue = ''
}

watch(() => dirty.value || saving.value, enabled => {
  if (enabled) globalThis.addEventListener('beforeunload', beforeUnload)
  else globalThis.removeEventListener('beforeunload', beforeUnload)
}, { immediate: true })
watch(() => form.value.name, () => { delete fieldErrors.value.name; error.value = '' })
watch(() => form.value.dkimSelector, () => { delete fieldErrors.value.dkimSelector; error.value = '' })

onBeforeRouteLeave(() => submitted.value || (!saving.value && (!dirty.value || globalThis.confirm('尚未创建邮件域，离开后已填写的内容将丢失。确定离开吗？'))))
onBeforeUnmount(() => globalThis.removeEventListener('beforeunload', beforeUnload))

function validate() {
  touched.value = { name: true, dkimSelector: true }
  fieldErrors.value = {}
  if (nameError.value) fieldErrors.value.name = nameError.value
  if (selectorError.value) fieldErrors.value.dkimSelector = selectorError.value
  return formValid.value
}

async function submit() {
  if (!validate()) return
  saving.value = true
  error.value = ''
  try {
    const created = await api<{ id: string }>('/domains', {
      method: 'POST',
      body: jsonBody({
        name: normalizedName.value,
        dkimSelector: form.value.dkimSelector.trim() || null,
        dkimPrivateKeyPath: null,
      }),
    })
    submitted.value = true
    await router.replace(`/domains/${created.id}`)
  } catch (cause) {
    if (cause instanceof ApiError && cause.problem.errors) {
      for (const [key, messages] of Object.entries(cause.problem.errors)) {
        const field = key.charAt(0).toLowerCase() + key.slice(1)
        if (messages[0]) fieldErrors.value[field] = messages[0]
      }
    }
    error.value = cause instanceof Error ? cause.message : '创建域失败'
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
    <v-btn variant="outlined" prepend-icon="mdi-arrow-left" to="/domains" :disabled="saving">返回列表</v-btn>
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
          :error-messages="fieldErrors.name || (touched.name ? nameError : '')"
          :disabled="!can('domain.manage') || saving"
          @blur="touched.name = true; form.name = normalizeDomainName(form.name)"
          required
          autofocus
        />

        <v-text-field
          v-model="form.dkimSelector"
          label="DKIM Selector（可选）"
          placeholder="mail"
          :error-messages="fieldErrors.dkimSelector || (touched.dkimSelector ? selectorError : '')"
          :disabled="!can('domain.manage') || saving"
          hint="用于生成 DKIM DNS 记录；留空可在域创建后使用推荐值"
          persistent-hint
          @blur="touched.dkimSelector = true; form.dkimSelector = form.dkimSelector.trim().toLowerCase()"
        />

        <v-alert type="info" variant="tonal" class="mb-5">域创建后，请前往“DNS 与安全”生成 DKIM 密钥并发布 MX、SPF、DKIM、DMARC 记录。检查失败只会持续告警，不会阻止 Direct MX 外发。</v-alert>

        <dl v-if="!nameError" class="create-preview mb-5"><div><dt>邮件域</dt><dd>{{ normalizedName }}</dd></div><div><dt>默认邮件主机</dt><dd>mail.{{ normalizedName }}</dd></div><div><dt>初始状态</dt><dd>启用 · Direct MX 待检查</dd></div></dl>

        <div class="create-form-actions">
          <v-spacer />
          <v-btn variant="outlined" to="/domains" :disabled="saving">取消</v-btn>
          <v-btn
            color="primary"
            type="submit"
            prepend-icon="mdi-plus"
            :loading="saving"
            :disabled="!can('domain.manage') || !formValid"
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

.create-preview { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; margin: 0; }
.create-preview div { padding: 12px 14px; border: 1px solid var(--border); border-radius: 8px; background: #f8fafc; }
.create-preview div:last-child { grid-column: 1 / -1; }
.create-preview dt { color: var(--text-muted); font-size: 12px; }
.create-preview dd { margin: 4px 0 0; font-weight: 600; overflow-wrap: anywhere; }
@media (max-width: 760px) { .create-preview { grid-template-columns: 1fr; } }
</style>
