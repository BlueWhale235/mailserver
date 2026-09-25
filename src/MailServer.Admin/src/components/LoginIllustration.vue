<template>
  <svg class="login-illustration" viewBox="0 0 720 560" fill="none" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
    <defs>
      <linearGradient id="login-card" x1="0" y1="0" x2="1" y2="1">
        <stop stop-color="#FFFFFF" />
        <stop offset="1" stop-color="#F8FAFF" />
      </linearGradient>
      <linearGradient id="login-mail" x1="0" y1="0" x2="1" y2="1">
        <stop stop-color="#6175E7" />
        <stop offset="1" stop-color="#475BCB" />
      </linearGradient>
    </defs>

    <circle cx="358" cy="282" r="190" fill="#E9EDFF" opacity=".65" />
    <circle cx="358" cy="282" r="143" fill="#F4F6FF" />

    <path class="flow" d="M171 177C237 199 250 230 296 258" stroke="#9AA8E9" stroke-width="2.4" stroke-linecap="round" />
    <path class="flow" d="M546 164C489 189 469 216 424 257" stroke="#9AA8E9" stroke-width="2.4" stroke-linecap="round" />
    <path class="flow" d="M159 395C235 361 258 344 302 315" stroke="#9AA8E9" stroke-width="2.4" stroke-linecap="round" />
    <path class="flow" d="M554 402C489 367 464 344 417 315" stroke="#9AA8E9" stroke-width="2.4" stroke-linecap="round" />

    <g class="float-a">
      <rect x="86" y="118" width="142" height="94" rx="22" fill="url(#login-card)" stroke="#DDE3F3" />
      <rect x="110" y="144" width="94" height="10" rx="5" fill="#D9DEEF" />
      <rect x="110" y="164" width="66" height="8" rx="4" fill="#E8EBF4" />
      <circle cx="196" cy="184" r="8" fill="#7D91E7" />
      <circle cx="172" cy="184" r="8" fill="#D9DEEF" />
    </g>

    <g class="float-b">
      <rect x="492" y="104" width="142" height="104" rx="22" fill="url(#login-card)" stroke="#DDE3F3" />
      <rect x="516" y="132" width="94" height="9" rx="4.5" fill="#D9DEEF" />
      <rect x="516" y="153" width="78" height="9" rx="4.5" fill="#E8EBF4" />
      <rect x="516" y="175" width="50" height="9" rx="4.5" fill="#E8EBF4" />
      <circle cx="602" cy="179" r="8" fill="#75BFA5" />
    </g>

    <g class="float-b">
      <rect x="74" y="352" width="150" height="102" rx="22" fill="url(#login-card)" stroke="#DDE3F3" />
      <path d="M105 383h87v42h-87z" stroke="#B7C0DB" stroke-width="2" />
      <path d="m108 387 40.5 27 40.5-27" stroke="#B7C0DB" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" />
    </g>

    <g class="float-a">
      <rect x="496" y="360" width="150" height="92" rx="22" fill="url(#login-card)" stroke="#DDE3F3" />
      <circle cx="529" cy="392" r="11" fill="#E2E7F7" />
      <rect x="550" y="384" width="68" height="9" rx="4.5" fill="#D8DDEF" />
      <rect x="550" y="402" width="49" height="8" rx="4" fill="#E9ECF5" />
    </g>

    <g>
      <circle class="pulse" cx="360" cy="280" r="94" fill="#8294F4" opacity=".18" />
      <rect x="274" y="210" width="172" height="142" rx="30" fill="url(#login-mail)" />
      <path d="M305 249h110v74H305z" fill="white" opacity=".97" />
      <path d="m307 253 53 38 53-38" stroke="#6076EF" stroke-width="4" stroke-linecap="round" stroke-linejoin="round" />
      <path d="m307 320 37-34M413 320l-37-34" stroke="#C8D0FA" stroke-width="3.2" stroke-linecap="round" />
      <circle cx="413" cy="224" r="19" fill="#75BFA5" stroke="#fff" stroke-width="5" />
      <path d="m405 224 5 5 10-11" stroke="white" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />
    </g>

    <circle cx="162" cy="176" r="7" fill="#8194E8" />
    <circle cx="550" cy="165" r="7" fill="#8194E8" />
    <circle cx="153" cy="397" r="7" fill="#8194E8" />
    <circle cx="558" cy="402" r="7" fill="#8194E8" />
  </svg>
</template>

<style scoped>
.login-illustration { display: block; width: min(92%, 680px); height: auto; overflow: visible; filter: drop-shadow(0 24px 55px rgba(62, 77, 148, .1)); }
.float-a { animation: login-float-a 5.5s ease-in-out infinite; transform-origin: center; }
.float-b { animation: login-float-b 6.5s ease-in-out infinite; transform-origin: center; }
.pulse { animation: login-pulse 2.8s ease-in-out infinite; transform-origin: center; }
.flow { stroke-dasharray: 8 10; animation: login-flow 10s linear infinite; }
@keyframes login-float-a { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(-8px); } }
@keyframes login-float-b { 0%, 100% { transform: translateY(0); } 50% { transform: translateY(7px); } }
@keyframes login-pulse { 0%, 100% { opacity: .55; } 50% { opacity: 1; } }
@keyframes login-flow { to { stroke-dashoffset: -180; } }
@media (prefers-reduced-motion: reduce) { .float-a, .float-b, .pulse, .flow { animation: none; } }
</style>
