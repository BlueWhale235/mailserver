<template>
  <section class="login-topology" aria-labelledby="login-topology-title">
    <h2 id="login-topology-title" class="sr-only">Direct MX 邮件投递链路</h2>
    <ol class="login-topology__route">
      <li>
        <span class="login-topology__node"><v-icon aria-hidden="true">mdi-web</v-icon></span>
        <strong>DNS</strong>
        <small>查询域名 MX 记录</small>
      </li>
      <li>
        <span class="login-topology__node"><v-icon aria-hidden="true">mdi-file-search-outline</v-icon></span>
        <strong>MX 解析</strong>
        <small>选择可用邮件服务器</small>
      </li>
      <li>
        <span class="login-topology__node login-topology__node--active"><v-icon aria-hidden="true">mdi-shield-check-outline</v-icon></span>
        <strong>TLS 投递</strong>
        <small>协商加密并安全传输</small>
      </li>
      <li>
        <span class="login-topology__node"><v-icon aria-hidden="true">mdi-server-outline</v-icon></span>
        <strong>远端服务器</strong>
        <small>完成目标域投递</small>
      </li>
    </ol>
  </section>
</template>
