<script setup lang="ts">withDefaults(defineProps<{ label: string; tone?: 'success'|'warning'|'danger'|'neutral' }>(), { tone: 'neutral' })</script>
<template><span class="status-pill" :class="`is-${tone}`"><i></i>{{ label }}</span></template>
