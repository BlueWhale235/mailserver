<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { usePermission } from '../composables/usePermission'
import AccountDirectoryView from './AccountDirectoryView.vue'
import DomainDetailView from './DomainDetailView.vue'

const route = useRoute()
const { can } = usePermission()
const canReadDomain = computed(() => can('domain.read', String(route.params.id)))
</script>

<template>
  <DomainDetailView v-if="canReadDomain" />
  <AccountDirectoryView v-else />
</template>
