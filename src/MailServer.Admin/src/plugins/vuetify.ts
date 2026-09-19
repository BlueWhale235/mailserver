import { createVuetify } from 'vuetify'
import * as components from 'vuetify/components'
import * as directives from 'vuetify/directives'

export default createVuetify({
  components,
  directives,
  theme: {
    defaultTheme: 'mailserverLight',
    themes: {
      mailserverLight: {
        dark: false,
        colors: {
          background: '#f3f6fb',
          surface: '#ffffff',
          primary: '#1769e0',
          secondary: '#526277',
          error: '#c43131',
          warning: '#c47a00',
          success: '#16865b',
          info: '#1769e0',
          'on-surface': '#172033',
          'on-primary': '#ffffff',
        },
        variables: {
          'border-color': '#e4e9f0',
          'focus-opacity': 0.08,
          'hover-opacity': 0.04,
        },
      },
    },
  },
  defaults: {
    VApp: { style: 'font-family: "Inter","Microsoft YaHei",system-ui,sans-serif' },
    VBtn: {
      rounded: 'lg',
      height: 40,
      fontWeight: 600,
      letterSpacing: '0.01em',
      elevation: 0,
      class: 'app-btn',
    },
    VCard: { elevation: 0, border: true, rounded: 'lg' },
    VTextField: {
      variant: 'outlined',
      density: 'compact',
      hideDetails: 'auto',
      color: 'primary',
    },
    VSelect: { variant: 'outlined', density: 'compact', hideDetails: 'auto', color: 'primary' },
    VTextarea: { variant: 'outlined', density: 'compact', hideDetails: 'auto', color: 'primary' },
    VCheckbox: { color: 'primary', density: 'compact' },
    VSwitch: { color: 'primary', density: 'compact' },
    VRadioGroup: { color: 'primary' },
    VChip: { rounded: 'lg' },
    VDataTableServer: { density: 'comfortable' },
    VDialog: { scrollable: true },
    VTooltip: { location: 'top' },
    VMenu: { transition: 'scale-transition', offset: 8 },
  },
})