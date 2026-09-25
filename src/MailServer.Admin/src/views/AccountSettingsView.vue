<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import { api, jsonBody } from "../lib/api";
import {
  storageSize,
  type DirectoryAccount,
  type DirectoryContext,
} from "../lib/accountDirectory";
import PageHeader from "../components/PageHeader.vue";
import DirectoryAliases from "../components/DirectoryAliases.vue";
import { usePageDraftGuard } from "../composables/usePageDraftGuard";
const route = useRoute(),
  router = useRouter();
const domainId = computed(() => String(route.params.id)),
  accountId = computed(() => String(route.params.accountId ?? ""));
const isNew = computed(() => route.name === "account-create");
type Section = "settings" | "aliases" | "password" | "danger";
const activeSection = ref<Section>("settings");
const context = ref<DirectoryContext | null>(null),
  account = ref<DirectoryAccount | null>(null);
const loading = ref(false),
  saving = ref(""),
  error = ref(""),
  notice = ref(""),
  deleteOpen = ref(false);
const localPart = ref(""),
  password = ref(""),
  confirm = ref("");
const draft = ref({ isEnabled: true, quotaGiB: 10 }),
  baseline = ref(JSON.stringify(draft.value));
const aliasDirty = ref(false),
  aliasBusy = ref(false),
  discardOpen = ref(false);
const settingsDirty = computed(
  () => JSON.stringify(draft.value) !== baseline.value,
);
const dirty = computed(
  () =>
    settingsDirty.value ||
    !!password.value ||
    !!confirm.value ||
    (isNew.value && !!localPart.value) ||
    aliasDirty.value,
);
const busy = computed(() => !!saving.value || aliasBusy.value);
const usagePercent = computed(() => {
  const used = account.value?.usedBytes ?? 0;
  const quota = account.value?.quotaBytes ?? 0;
  return quota > 0 ? Math.min(100, Math.round((used / quota) * 100)) : 0;
});
const availableSections = computed(() => {
  if (isNew.value) return [];
  const items: { key: Section; title: string; description: string; icon: string }[] = [];
  if (context.value?.canReadAccounts) items.push({ key: "settings", title: "账号设置", description: "状态与容量", icon: "mdi-tune-variant" });
  if (context.value?.canReadAliases) items.push({ key: "aliases", title: "关联别名", description: "地址与收发模式", icon: "mdi-at" });
  if (context.value?.canManageAccounts) items.push({ key: "password", title: "密码设置", description: "更新登录凭据", icon: "mdi-lock-outline" });
  if (context.value?.canManageAccounts) items.push({ key: "danger", title: "危险操作", description: "账号软删除", icon: "mdi-alert-outline" });
  return items;
});
const { leaveOpen, decideLeave } = usePageDraftGuard(dirty, busy);
const settingsValid = computed(
  () =>
    Number.isFinite(draft.value.quotaGiB) &&
    draft.value.quotaGiB > 0 &&
    draft.value.quotaGiB * 1024 ** 3 >= (account.value?.usedBytes ?? 0),
);
const passwordValid = computed(
  () => password.value.length >= 12 && password.value === confirm.value,
);
const createValid = computed(
  () =>
    /^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+$/.test(localPart.value.trim()) &&
    settingsValid.value &&
    passwordValid.value,
);
const back = computed(() => ({
  name: "account-directory",
  params: { id: domainId.value },
  query: route.query,
}));
let request = 0;
async function load() {
  const current = ++request;
  loading.value = true;
  error.value = "";
  context.value = null;
  account.value = null;
  localPart.value = "";
  password.value = "";
  confirm.value = "";
  aliasDirty.value = false;
  draft.value = { isEnabled: true, quotaGiB: 10 };
  baseline.value = JSON.stringify(draft.value);
  try {
    const next = await api<DirectoryContext>(
      `/domains/${domainId.value}/account-directory`,
    );
    const row = isNew.value
      ? null
      : await api<DirectoryAccount>(
          `/domains/${domainId.value}/account-directory/accounts/${accountId.value}`,
        );
    if (current !== request) return;
    context.value = next;
    account.value = row;
    if (!isNew.value) activeSection.value = next.canReadAccounts ? "settings" : "aliases";
    if (row && next.canReadAccounts) resetSettings();
  } catch (cause) {
    if (current === request) error.value = (cause as Error).message;
  } finally {
    if (current === request) loading.value = false;
  }
}
function resetSettings() {
  draft.value = {
    isEnabled: account.value?.isEnabled ?? true,
    quotaGiB: (account.value?.quotaBytes ?? 10 * 1024 ** 3) / 1024 ** 3,
  };
  baseline.value = JSON.stringify(draft.value);
}
async function refreshSummary() {
  try {
    account.value = await api<DirectoryAccount>(
      `/domains/${domainId.value}/account-directory/accounts/${accountId.value}`,
    );
  } catch (cause) {
    error.value = (cause as Error).message;
  }
}
watch(
  () => [route.params.id, route.params.accountId, route.name],
  () => {
    void load();
  },
  { immediate: true },
);
onBeforeUnmount(() => {
  request++;
  password.value = "";
  confirm.value = "";
});
async function create() {
  if (!createValid.value || busy.value || !context.value?.canManageAccounts)
    return;
  saving.value = "create";
  error.value = "";
  try {
    const row = await api<DirectoryAccount>(
      `/domains/${domainId.value}/accounts`,
      {
        method: "POST",
        body: jsonBody({
          localPart: localPart.value.trim(),
          password: password.value,
          quotaBytes: Math.round(draft.value.quotaGiB * 1024 ** 3),
        }),
      },
    );
    password.value = "";
    confirm.value = "";
    localPart.value = "";
    baseline.value = JSON.stringify(draft.value);
    saving.value = "";
    await router.replace({
      name: "account-settings",
      params: { id: domainId.value, accountId: row.id },
      query: route.query,
    });
    notice.value = "账号已创建，可以继续添加别名。";
  } catch (cause) {
    error.value = (cause as Error).message;
  } finally {
    saving.value = "";
  }
}
async function saveSettings() {
  if (
    !settingsValid.value ||
    !settingsDirty.value ||
    busy.value ||
    !context.value?.canManageAccounts
  )
    return;
  saving.value = "settings";
  error.value = "";
  notice.value = "";
  try {
    await api(`/accounts/${accountId.value}`, {
      method: "PATCH",
      body: jsonBody({
        isEnabled: draft.value.isEnabled,
        quotaBytes: Math.round(draft.value.quotaGiB * 1024 ** 3),
      }),
    });
    baseline.value = JSON.stringify(draft.value);
    notice.value = "账号设置已保存。";
    await refreshSummary();
    aliasRefresh.value++;
  } catch (cause) {
    error.value = (cause as Error).message;
  } finally {
    saving.value = "";
  }
}
const aliasRefresh = ref(0);
async function savePassword() {
  if (!passwordValid.value || busy.value || !context.value?.canManageAccounts)
    return;
  saving.value = "password";
  error.value = "";
  notice.value = "";
  try {
    await api(`/accounts/${accountId.value}/password`, {
      method: "POST",
      body: jsonBody({ password: password.value }),
    });
    password.value = "";
    confirm.value = "";
    notice.value = "密码已更新。";
  } catch (cause) {
    error.value = (cause as Error).message;
  } finally {
    saving.value = "";
  }
}
async function remove() {
  if (
    busy.value ||
    account.value?.isEnabled !== false ||
    !context.value?.canManageAccounts
  )
    return;
  saving.value = "delete";
  error.value = "";
  try {
    await api(`/accounts/${accountId.value}`, { method: "DELETE" });
    password.value = "";
    confirm.value = "";
    aliasDirty.value = false;
    baseline.value = JSON.stringify(draft.value);
    deleteOpen.value = false;
    saving.value = "";
    await router.replace(back.value);
  } catch (cause) {
    error.value = (cause as Error).message;
  } finally {
    saving.value = "";
  }
}
</script>
<template>
  <div class="account-page">
    <div class="account-breadcrumb">
      <v-btn variant="text" size="small" prepend-icon="mdi-arrow-left" :to="back">账号和别名</v-btn>
      <span>/</span><span>{{ isNew ? "新建账号" : "账号详情" }}</span>
    </div>
    <PageHeader
      :title="isNew ? '新建邮箱账号' : '邮箱账号设置'"
      :description="context ? context.domainName + ' · 管理账号状态、容量、密码和关联别名' : error ? '账号信息加载失败' : '正在加载…'"
    >
      <v-btn v-if="!isNew && context && !dirty" variant="outlined" prepend-icon="mdi-refresh" :disabled="busy || loading" @click="load">刷新</v-btn>
    </PageHeader>
    <v-alert v-if="error" type="error" variant="tonal" class="mb-4">{{ error }}<v-btn v-if="!context" variant="text" @click="load">重试</v-btn></v-alert>
    <v-alert v-if="notice" type="success" variant="tonal" class="mb-4">{{ notice }}</v-alert>
    <v-progress-linear v-if="loading" indeterminate color="primary" />
    <template v-else-if="context && (isNew || account)">
      <v-card v-if="isNew && context.canManageAccounts" flat class="account-card create-card">
        <div class="card-heading"><h2>账号信息</h2><p>创建成功后会进入账号详情，可继续添加别名。</p></div>
        <div class="card-body">
          <div class="settings-form">
            <v-text-field v-model="localPart" label="邮箱本地部分" :suffix="'@' + context.domainName" :disabled="busy" />
            <v-text-field v-model.number="draft.quotaGiB" label="容量配额（GiB）" type="number" min="0" step="any" :disabled="busy" />
            <v-text-field v-model="password" label="初始密码（至少 12 个字符）" type="password" autocomplete="new-password" :disabled="busy" />
            <v-text-field v-model="confirm" label="再次输入密码" type="password" autocomplete="new-password" :disabled="busy" />
          </div>
        </div>
        <div class="card-actions"><v-btn variant="text" :disabled="busy" :to="back">取消</v-btn><v-btn color="primary" :loading="saving === 'create'" :disabled="!createValid || busy" @click="create">创建账号</v-btn></div>
      </v-card>
      <v-alert v-else-if="isNew" type="warning">没有创建账号的权限。</v-alert>
      <template v-else>
        <section class="account-overview" aria-label="账号概览">
          <div class="account-identity">
            <div class="account-avatar"><v-icon size="25">mdi-email-outline</v-icon></div>
            <div class="identity-copy">
              <div class="identity-line">
                <strong>{{ account?.address }}</strong>
                <v-chip v-if="context.canReadAccounts" size="small" :color="account?.isEnabled ? 'success' : 'default'" variant="tonal">{{ account?.isEnabled ? "已启用" : "已停用" }}</v-chip>
              </div>
              <p>{{ context.domainName }} · 主邮箱账号</p>
            </div>
          </div>
          <div class="overview-stats">
            <div v-if="context.canReadAccounts" class="overview-stat"><span>容量使用</span><strong>{{ storageSize(account?.usedBytes ?? null) }} / {{ storageSize(account?.quotaBytes ?? null) }}</strong></div>
            <div v-if="context.canReadAliases" class="overview-stat"><span>关联别名</span><strong>{{ account?.aliasCount ?? 0 }} 个</strong></div>
          </div>
        </section>

        <div class="account-workspace">
          <nav class="account-nav" aria-label="账号详情分区">
            <button v-for="section in availableSections" :key="section.key" type="button" :class="{ active: activeSection === section.key }" @click="activeSection = section.key">
              <v-icon size="20">{{ section.icon }}</v-icon>
              <span><b>{{ section.title }}</b><small>{{ section.description }}</small></span>
              <i v-if="section.key === 'settings' && settingsDirty" class="draft-dot" title="有未保存修改" />
              <i v-else-if="section.key === 'aliases' && aliasDirty" class="draft-dot" title="有未保存修改" />
              <v-icon v-else size="16">mdi-chevron-right</v-icon>
            </button>
            <div class="nav-note"><v-icon size="17">mdi-information-outline</v-icon><span>账号设置、密码和别名分别保存。切换分区时未保存的草稿会保留。</span></div>
          </nav>
          <div class="section-content">
            <div v-if="context.canReadAccounts" v-show="activeSection === 'settings'" class="settings-layout">
              <v-card flat class="account-card">
                <div class="card-heading"><h2>账号基础设置</h2><p>邮箱地址不可修改，如需增加收发地址可使用关联别名。</p></div>
                <div class="card-body">
                  <v-text-field :model-value="account?.address" label="邮箱地址" readonly />
                  <div class="setting-toggle">
                    <div><strong>启用账号</strong><p>停用后用户无法登录，关联别名也会停止接收邮件。</p></div>
                    <v-switch v-model="draft.isEnabled" aria-label="启用账号" color="primary" hide-details :disabled="!context.canManageAccounts || busy || aliasDirty" />
                  </div>
                  <v-alert v-if="!draft.isEnabled" type="warning" variant="tonal" class="mb-4">账号停用后无法登录，其关联别名也将不可用。</v-alert>
                  <div class="quota-header"><div><span>邮箱容量配额</span><strong>{{ storageSize(account?.usedBytes ?? null) }} <small>已使用</small></strong></div><v-text-field v-model.number="draft.quotaGiB" label="总配额（GiB）" type="number" min="0" step="any" :disabled="!context.canManageAccounts || busy" :error-messages="!settingsValid ? '配额必须大于零，且不能小于已用容量。' : undefined" /></div>
                  <v-progress-linear :model-value="usagePercent" color="primary" height="8" rounded />
                  <div class="quota-caption"><span>0 GiB</span><span>已使用 {{ usagePercent }}%</span></div>
                </div>
                <div v-if="context.canManageAccounts" class="card-actions">
                  <v-btn variant="text" :disabled="!settingsDirty || busy" @click="discardOpen = true">撤销修改</v-btn>
                  <v-btn color="primary" :disabled="!settingsValid || !settingsDirty || busy || aliasDirty" :loading="saving === 'settings'" @click="saveSettings">保存账号设置</v-btn>
                </div>
              </v-card>
              <aside class="info-stack">
                <v-card flat class="account-card info-card"><h3>账号信息</h3><div class="info-row"><span>账号类型</span><strong>邮箱账号</strong></div><div class="info-row"><span>所属域名</span><strong>{{ context.domainName }}</strong></div><div class="info-row"><span>邮箱地址</span><strong>{{ account?.address }}</strong></div><v-alert type="info" variant="tonal" density="compact" class="mt-4">如果只需增加收件地址，建议创建别名。</v-alert></v-card>
                <v-card flat class="account-card info-card"><h3>登录与投递</h3><div class="info-row"><span>账号登录</span><strong>{{ account?.isEnabled ? "允许" : "停用" }}</strong></div><div class="info-row"><span>账号收信</span><strong>{{ account?.isEnabled ? "可用" : "不可用" }}</strong></div><div v-if="context.canReadAliases" class="info-row"><span>关联别名</span><strong>{{ account?.aliasCount ?? 0 }} 个</strong></div></v-card>
              </aside>
            </div>
            <div v-if="context.canReadAliases" v-show="activeSection === 'aliases'" class="alias-section">
              <DirectoryAliases
                :key="accountId + '-' + aliasRefresh"
                :domain-id="domainId"
                :account-id="accountId"
                :account-address="account?.address"
                :can-manage="context.canManageAliases && !saving"
                @dirty="aliasDirty = $event"
                @busy="aliasBusy = $event"
                @changed="refreshSummary"
              />
            </div>
            <v-card v-if="context.canManageAccounts" v-show="activeSection === 'password'" flat class="account-card">
              <div class="card-heading"><h2>修改密码</h2><p>更新后旧密码立即失效，已登录的邮件客户端可能需要重新认证。</p></div>
              <div class="card-body password-fields"><v-text-field v-model="password" label="新密码（至少 12 个字符）" type="password" autocomplete="new-password" :disabled="busy" /><v-text-field v-model="confirm" label="再次输入新密码" type="password" autocomplete="new-password" :disabled="busy" /></div>
              <div class="card-actions"><v-btn color="primary" :disabled="!passwordValid || busy" :loading="saving === 'password'" @click="savePassword">更新密码</v-btn></div>
            </v-card>
            <v-card v-if="context.canManageAccounts" v-show="activeSection === 'danger'" flat class="account-card danger-card">
              <div class="card-heading"><h2>危险操作</h2><p>这些操作会影响用户登录和邮件收取。</p></div>
              <div class="card-body danger-row"><div><strong>软删除账号</strong><p>请先停用账号。删除后账号进入回收站，关联别名可能需要重新绑定。</p></div><v-btn color="error" variant="tonal" :disabled="account?.isEnabled !== false || busy || dirty" @click="error = ''; deleteOpen = true">软删除账号</v-btn></div>
            </v-card>
          </div>
        </div>
      </template>
    </template>
    <v-dialog v-model="deleteOpen" max-width="500" :persistent="busy"><v-card title="确认软删除账号"><v-card-text>{{ account?.address }} 将进入回收站。<v-alert v-if="error" type="error">{{ error }}</v-alert></v-card-text><v-card-actions class="justify-end"><v-btn :disabled="busy" @click="deleteOpen = false">取消</v-btn><v-btn color="error" :disabled="busy" :loading="saving === 'delete'" @click="remove">确认软删除</v-btn></v-card-actions></v-card></v-dialog>
    <v-dialog v-model="discardOpen" max-width="440"><v-card title="撤销账号设置修改？"><v-card-text>账号设置将恢复为最近读取的内容。</v-card-text><v-card-actions class="justify-end"><v-btn @click="discardOpen = false">继续编辑</v-btn><v-btn color="error" @click="resetSettings(); discardOpen = false">确认撤销</v-btn></v-card-actions></v-card></v-dialog>
    <v-dialog v-model="leaveOpen" max-width="440" @update:model-value="value => { if (!value) decideLeave(false) }"><v-card title="放弃未保存的修改？"><v-card-text>离开后当前草稿将丢失。</v-card-text><v-card-actions class="justify-end"><v-btn @click="decideLeave(false)">继续编辑</v-btn><v-btn color="error" @click="decideLeave(true)">放弃修改并离开</v-btn></v-card-actions></v-card></v-dialog>
  </div>
</template>
<style scoped>
.account-page { --account-line: #e5eaf2; --account-muted: #667085; }
.account-breadcrumb { display: flex; align-items: center; gap: 8px; margin-bottom: 12px; color: var(--account-muted); font-size: 13px; }
.account-breadcrumb :deep(.v-btn) { padding-left: 0; }
.account-page :deep(.page-header) { margin-bottom: 20px; }
.account-overview { display: flex; align-items: center; justify-content: space-between; gap: 24px; padding: 23px 25px; margin-bottom: 20px; border: 1px solid var(--account-line); border-radius: 14px; background: linear-gradient(135deg, #fff, #fbfbff); box-shadow: 0 1px 3px rgba(24, 37, 61, .04); }
.account-identity, .identity-line, .overview-stats { display: flex; align-items: center; gap: 14px; }
.account-avatar { display: grid; place-items: center; width: 52px; height: 52px; flex: none; border: 1px solid #dfe1ff; border-radius: 13px; background: #efefff; color: #5b5bd6; }
.identity-copy { min-width: 0; }
.identity-line { flex-wrap: wrap; gap: 10px; }
.identity-line strong { color: #172033; font-size: 19px; overflow-wrap: anywhere; }
.identity-copy p { margin: 5px 0 0; color: var(--account-muted); font-size: 12px; }
.overview-stat { min-width: 135px; padding: 11px 14px; border: 1px solid var(--account-line); border-radius: 10px; background: #fff; }
.overview-stat span, .overview-stat strong { display: block; }
.overview-stat span { color: var(--account-muted); font-size: 11px; }
.overview-stat strong { margin-top: 4px; color: #172033; font-size: 14px; white-space: nowrap; }
.account-workspace { display: grid; grid-template-columns: 238px minmax(0, 1fr); align-items: start; gap: 18px; }
.account-nav { position: sticky; top: 76px; overflow: hidden; border: 1px solid var(--account-line); border-radius: 10px; background: #fff; }
.account-nav button { display: grid; grid-template-columns: 24px minmax(0, 1fr) 16px; align-items: center; gap: 10px; width: 100%; padding: 14px; border: 0; border-bottom: 1px solid var(--account-line); color: #667085; background: transparent; text-align: left; cursor: pointer; }
.account-nav button:hover { background: #f8fafc; }
.account-nav button.active { color: #175cb8; background: #f2f7ff; box-shadow: inset 3px 0 #2c72d6; }
.account-nav button span { display: flex; flex-direction: column; gap: 2px; }
.account-nav b { font-size: 13px; }
.account-nav small { color: #84909f; font-size: 11px; }
.draft-dot { width: 8px; height: 8px; border-radius: 50%; background: #e9a23b; }
.nav-note { display: flex; gap: 8px; padding: 12px 14px; color: #84909f; background: #fafbfc; font-size: 11px; line-height: 1.6; }
.section-content { min-width: 0; }
.account-card { min-width: 0; overflow: hidden; border: 1px solid var(--account-line); border-radius: 12px; background: #fff; }
.card-heading { padding: 20px 22px 17px; border-bottom: 1px solid var(--account-line); }
.card-heading h2 { margin: 0; color: #172033; font-size: 17px; }
.card-heading p { margin: 5px 0 0; color: var(--account-muted); font-size: 12px; line-height: 1.55; }
.card-body { padding: 22px; }
.card-body :deep(.v-input) { margin-bottom: 14px; }
.card-actions { display: flex; justify-content: flex-end; gap: 10px; padding: 14px 22px; border-top: 1px solid var(--account-line); background: #fcfcfd; }
.settings-layout { display: grid; grid-template-columns: minmax(0, 1fr) 290px; gap: 18px; align-items: start; }
.setting-toggle { display: flex; align-items: center; justify-content: space-between; gap: 20px; padding: 15px 0; margin-bottom: 18px; border-block: 1px solid var(--account-line); }
.setting-toggle strong, .danger-row strong { color: #172033; font-size: 14px; }
.setting-toggle p, .danger-row p { margin: 5px 0 0; color: var(--account-muted); font-size: 12px; line-height: 1.5; }
.setting-toggle :deep(.v-switch) { flex: none; }
.quota-header { display: flex; align-items: end; justify-content: space-between; gap: 18px; }
.quota-header > div:first-child { display: grid; gap: 6px; padding-bottom: 18px; }
.quota-header span { color: var(--account-muted); font-size: 12px; }
.quota-header strong { color: #172033; font-size: 18px; }
.quota-header small { color: var(--account-muted); font-size: 11px; font-weight: 400; }
.quota-header :deep(.v-input) { max-width: 170px; }
.quota-caption { display: flex; justify-content: space-between; margin-top: 7px; color: #98a2b3; font-size: 11px; }
.info-stack { display: grid; gap: 18px; }
.info-card { padding: 18px; }
.info-card h3 { margin: 0 0 12px; font-size: 14px; }
.info-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 11px 0; border-top: 1px solid var(--account-line); }
.info-row span { color: var(--account-muted); font-size: 12px; }
.info-row strong { color: #344054; font-size: 12px; text-align: right; overflow-wrap: anywhere; }
.alias-section :deep(.alias-directory) { border: 1px solid var(--account-line); border-radius: 12px; }
.password-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px; }
.danger-card { border-color: #f2dada; }
.danger-card .card-heading { border-bottom-color: #f2dada; }
.danger-card h2 { color: #8d3333; }
.danger-row { display: flex; align-items: center; justify-content: space-between; gap: 18px; }
.create-card { max-width: 780px; }
.settings-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px 18px; }
@media (max-width: 1300px) { .settings-layout { grid-template-columns: 1fr; } .info-stack { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 1050px) { .account-workspace { grid-template-columns: 1fr; } .account-nav { position: static; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); } .account-nav button:nth-child(2) { border-right: 0; } .nav-note { grid-column: 1 / -1; } }
@media (max-width: 700px) { .account-overview { align-items: start; flex-direction: column; } .overview-stats { width: 100%; } .overview-stat { flex: 1; min-width: 0; } .info-stack, .password-fields, .settings-form { grid-template-columns: 1fr; } .quota-header { align-items: start; flex-direction: column; } .quota-header :deep(.v-input) { max-width: none; width: 100%; } .danger-row { align-items: start; flex-direction: column; } }
</style>
