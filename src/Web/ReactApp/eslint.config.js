import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { globalIgnores } from 'eslint/config'
import localPlugin from './eslint-rules/eslint-plugin-local.js'

export default tseslint.config([
  globalIgnores(['dist', 'coverage', 'coverage/**', 'coverage/lcov-report/**']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat['recommended-latest'],
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    plugins: {
      local: localPlugin,
    },
    rules: {
      'local/pf-no-unguarded-console': 'warn',
      'local/pf-no-raw-html-controls': 'warn',
      'local/pf-require-apiclient': 'error',
      'local/pf-no-inert-state-bg': 'error',
      // DESIGN-LANGUAGE caps rectangular surfaces at --pf-radius-lg (8px) and
      // reserves the fully-round radius for shapes that are actually round.
      // This ran in two tiers while the backlog was cleared (#1015): a
      // grandfathered 12px ceiling repo-wide, and the real line only in the
      // areas epic #1005 had migrated. #1022 adjudicated the remaining
      // `rounded-full` call sites and flattened the `rounded-xl` ones, so
      // there is one tier now and it is the documented one.
      'local/pf-no-oversized-radius': ['error', { maxPx: 8, checkFullRound: true }],
    },
  },
  // Exclude api.ts from apiClient rule (it defines apiClient)
  {
    files: ['src/services/api.ts'],
    rules: {
      'local/pf-require-apiclient': 'off',
    },
  },
  // Exclude httpClient.ts from apiClient rule: it is the sanctioned shared
  // axios instance + interceptors that api.ts and every per-domain module
  // under src/services/api/ build on (see issue #2343). It legitimately
  // creates the axios instance the rule is designed to guard against
  // elsewhere.
  {
    files: ['src/services/api/httpClient.ts'],
    rules: {
      'local/pf-require-apiclient': 'off',
    },
  },
  // Disable raw HTML controls rule in test files (tests often need raw elements for testing)
  {
    files: ['src/test/**/*.{ts,tsx}', '**/*.test.{ts,tsx}', '**/*.spec.{ts,tsx}'],
    rules: {
      'local/pf-no-raw-html-controls': 'off',
    },
  },
  // Playwright e2e specs call fetch() inside page.evaluate(), which runs in
  // the browser page context with no access to the app's apiClient module.
  // The pf-require-apiclient rule's premise doesn't apply there. This
  // exclusion is needed now that a CodeQL js/overwritten-property fix
  // (merging duplicate CallExpression handlers in pf-require-apiclient.js)
  // makes the rule correctly detect these previously-unreported fetch calls.
  {
    files: ['e2e/**/*.ts'],
    rules: {
      'local/pf-require-apiclient': 'off',
    },
  },
])
