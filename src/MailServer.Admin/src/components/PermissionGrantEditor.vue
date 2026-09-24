<script setup lang="ts">
import PermissionDomainPicker from './PermissionDomainPicker.vue'

interface PermissionCatalogItem {
  code: string
  name: string
  description: string
  groupKey: string
  supportsDomainScope: boolean
}

interface PermissionCatalogGroup {
  key: string
  name: string
  permissions: PermissionCatalogItem[]
}

interface PermissionGrant {
  permissionCode: string
  resourceType: string
  resourceKey: string
}

withDefaults(defineProps<{
  catalog: PermissionCatalogGroup[]
  disabled?: boolean
}>(), { disabled: false })

const grants = defineModel<PermissionGrant[]>({ required: true })

function grantsFor(code: string) {
  return grants.value.filter(grant => grant.permissionCode === code)
}

function isSelected(code: string) {
  return grantsFor(code).length > 0
}

function toggle(permission: PermissionCatalogItem, selected: boolean | null) {
  const remaining = grants.value.filter(grant => grant.permissionCode !== permission.code)
  if (!selected) {
    grants.value = remaining
    return
  }

  if (permission.code === '*') {
    grants.value = [{ permissionCode: '*', resourceType: '', resourceKey: '' }]
    return
  }

  const withoutFullAccess = remaining.filter(grant => grant.permissionCode !== '*')
  grants.value = [
    ...withoutFullAccess,
    permission.supportsDomainScope
      ? { permissionCode: permission.code, resourceType: 'domain', resourceKey: '*' }
      : { permissionCode: permission.code, resourceType: '', resourceKey: '' },
  ]
}

function scopeValues(code: string) {
  return grantsFor(code).map(grant => grant.resourceKey || '*')
}

function updateScopes(permission: PermissionCatalogItem, values: string[] | null) {
  const remaining = grants.value.filter(grant => grant.permissionCode !== permission.code)
  const selected = [...new Set(values ?? [])]
  const normalized = selected.includes('*') ? ['*'] : selected
  grants.value = [
    ...remaining,
    ...normalized.map(resourceKey => ({
      permissionCode: permission.code,
      resourceType: 'domain',
      resourceKey,
    })),
  ]
}

function selectedCount(group: PermissionCatalogGroup) {
  return group.permissions.filter(permission => isSelected(permission.code)).length
}
</script>

<template>
  <div class="permission-editor">
    <section v-for="group in catalog" :key="group.key" class="permission-group">
      <header>
        <div><h3>{{ group.name }}</h3><span>{{ group.permissions.length }} 项权限</span></div>
        <span v-if="selectedCount(group)" class="permission-count">已选 {{ selectedCount(group) }}</span>
      </header>
      <div class="permission-list">
        <article v-for="permission in group.permissions" :key="permission.code" :class="{ 'is-selected': isSelected(permission.code) }">
          <v-checkbox
            :model-value="isSelected(permission.code)"
            :label="permission.name"
            :disabled="disabled"
            hide-details
            @update:model-value="value => toggle(permission, value)"
          />
          <div class="permission-description">
            <p>{{ permission.description }}</p>
            <code>{{ permission.code }}</code>
          </div>
          <PermissionDomainPicker
            v-if="permission.supportsDomainScope && isSelected(permission.code)"
            :model-value="scopeValues(permission.code)"
            :disabled="disabled"
            class="permission-scope"
            @update:model-value="value => updateScopes(permission, value)"
          />
        </article>
      </div>
    </section>
  </div>
</template>

<style scoped>
.permission-editor { display: grid; gap: 18px; }
.permission-group { overflow: hidden; border: 1px solid var(--border); border-radius: 10px; background: #fff; }
.permission-group > header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 13px 16px; background: #f8fafd; border-bottom: 1px solid var(--border); }
.permission-group h3 { margin: 0; color: var(--text); font-size: 14px; }
.permission-group header span { color: var(--text-muted); font-size: 11px; }
.permission-group .permission-count { color: var(--color-primary); font-weight: 700; }
.permission-list article { display: grid; grid-template-columns: minmax(190px, .7fr) minmax(240px, 1fr); align-items: start; gap: 6px 18px; padding: 10px 16px 12px; border-bottom: 1px solid #edf0f4; }
.permission-list article:last-child { border-bottom: 0; }
.permission-list article.is-selected { background: #fbfdff; }
.permission-list .v-checkbox { min-height: 36px; }
.permission-description { padding-top: 9px; }
.permission-description p { margin: 0 0 3px; color: var(--text-secondary); font-size: 12px; line-height: 1.5; }
.permission-description code { color: var(--text-muted); font-size: 11px; }
.permission-scope { grid-column: 1 / -1; }
@media (max-width: 720px) {
  .permission-list article { grid-template-columns: 1fr; }
  .permission-description { padding: 0 0 4px 38px; }
  .permission-scope { grid-column: 1; }
}
</style>
