import js from '@eslint/js'
import vue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'

const browserGlobals = {
  AbortController: 'readonly',
  BeforeUnloadEvent: 'readonly',
  Blob: 'readonly',
  crypto: 'readonly',
  document: 'readonly',
  fetch: 'readonly',
  File: 'readonly',
  FileReader: 'readonly',
  FormData: 'readonly',
  Headers: 'readonly',
  localStorage: 'readonly',
  navigator: 'readonly',
  requestAnimationFrame: 'readonly',
  Response: 'readonly',
  sessionStorage: 'readonly',
  TextDecoder: 'readonly',
  URL: 'readonly',
  URLSearchParams: 'readonly',
  window: 'readonly'
}

export default tseslint.config(
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...vue.configs['flat/essential'],
  { ignores: ['dist/**', 'node_modules/**'] },
  {
    files: ['**/*.vue'],
    languageOptions: { globals: browserGlobals, parserOptions: { parser: tseslint.parser } },
    rules: { 'vue/multi-word-component-names': 'off' }
  }
)
