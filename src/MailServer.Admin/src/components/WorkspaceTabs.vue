<script setup lang="ts">
interface WorkspaceTab {
  value: string
  label: string
  count?: number
  hint?: string
}

defineProps<{ items: WorkspaceTab[]; modelValue: string; label: string }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
</script>

<template>
  <nav class="workspace-tabs" :aria-label="label">
    <button
      v-for="item in items"
      :key="item.value"
      type="button"
      :class="{ active: modelValue === item.value }"
      :aria-current="modelValue === item.value ? 'page' : undefined"
      @click="emit('update:modelValue', item.value)"
    >
      <span>{{ item.label }}</span>
      <small v-if="item.count !== undefined">
        {{ item.count.toLocaleString('zh-CN') }}<em v-if="item.hint"> {{ item.hint }}</em>
      </small>
    </button>
  </nav>
</template>

<style scoped>
.workspace-tabs { display: flex; align-items: stretch; gap: 2px; margin-bottom: 14px; padding: 4px; border: 1px solid var(--border); border-radius: 10px; background: #fff; box-shadow: var(--shadow-card); overflow-x: auto; }
.workspace-tabs button { min-width: 126px; display: flex; align-items: center; justify-content: center; gap: 8px; border: 0; border-radius: 7px; padding: 10px 13px; background: transparent; color: var(--text-secondary); cursor: pointer; font: 600 12px/1.2 var(--font-family); white-space: nowrap; transition: color .15s ease, background .15s ease; }
.workspace-tabs button:hover { background: #f7f9fc; color: var(--text); }
.workspace-tabs button.active { background: var(--color-primary-soft); color: var(--color-primary-strong); }
.workspace-tabs small { min-width: 22px; padding: 2px 6px; border-radius: 999px; background: #eef1f5; color: var(--text-secondary); font-size: 10px; font-variant-numeric: tabular-nums; }
.workspace-tabs button.active small { background: #dceaff; color: var(--color-primary-strong); }
.workspace-tabs em { font-style: normal; opacity: .72; }
@media (max-width: 1100px) { .workspace-tabs { justify-content: flex-start; } }
</style>
