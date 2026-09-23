<script setup lang="ts">
import { computed } from 'vue'
import PageHeader from '../components/PageHeader.vue'
import { useAuthStore } from '../stores/auth'
import { canAccessPage, systemPages } from '../lib/pageAccess'

const auth = useAuthStore()
const visibleItems = computed(() => systemPages.filter(item => canAccessPage(item, auth)))
</script>

<template>
  <PageHeader title="系统" description="管理员、集成、维护任务、审计与全局运行参数" />
  <div class="open-list settings-list">
    <article
      v-for="item in visibleItems"
      :key="item.routeName"
    >
      <span class="module-icon"><v-icon>{{ item.icon }}</v-icon></span>
      <div><h2>{{ item.title }}</h2><p>{{ item.description }}</p></div>
      <v-btn icon="mdi-chevron-right" variant="text" :to="item.path" :aria-label="`打开${item.title}`" />
    </article>
  </div>
</template>
