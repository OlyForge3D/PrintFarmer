/**
 * ESLint rule: pf-require-apiclient
 * - Enforces that all REST API calls use apiClient from '@/services/api'
 * - Detects and warns on:
 *   - Direct axios imports/usage (axios.get, axios.post, axios.create, etc.)
 *   - Direct fetch calls for API endpoints
 *   - Creating custom axios instances
 * - Exceptions: axios.isAxiosError, AxiosError type usage in error handling
 */
export default {
  meta: {
    type: 'problem',
    docs: {
      description: 'Enforce use of apiClient singleton for all REST API calls',
      recommended: true
    },
    messages: {
      useApiClient: 'Use apiClient from "@/services/api" for all REST API calls. Avoid direct axios or fetch.',
      useApiClientForCreate: 'Do not create custom axios instances. Use apiClient from "@/services/api" instead.',
      useApiClientForFetch: 'Use apiClient from "@/services/api" instead of fetch for REST API calls.',
      useApiClientForAxios: 'Use apiClient from "@/services/api" instead of direct axios calls.',
      exceptionImport: 'axios.isAxiosError and AxiosError are acceptable for error type checking only, not for API calls.'
    },
    schema: []
  },

  create(context) {
    const sourceCode = context.sourceCode;
    let hasApiClientImport = false;
    const allowedAxiosUsage = ['isAxiosError']; // Methods allowed without apiClient

    return {
      // Track if apiClient is imported
      ImportDeclaration(node) {
        if (node.source.value === '@/services/api') {
          node.specifiers.forEach(spec => {
            if (spec.imported?.name === 'apiClient' || spec.local?.name === 'apiClient') {
              hasApiClientImport = true;
            }
          });
        }
      },

      // Detect: import axios from 'axios'
      ImportDeclaration(node) {
        if (node.source.value === 'axios') {
          // Check if it's importing more than just type definitions or error utilities
          const hasAxiosDefault = node.specifiers.some(
            spec => spec.type === 'ImportDefaultSpecifier' && spec.local.name === 'axios'
          );
          
          if (hasAxiosDefault) {
            context.report({
              node,
              messageId: 'useApiClient',
              data: { usage: 'axios import' }
            });
          }
        }
      },

      // Detect: axios.create(...)
      CallExpression(node) {
        if (
          node.callee.type === 'MemberExpression' &&
          node.callee.object.name === 'axios' &&
          node.callee.property.name === 'create'
        ) {
          context.report({
            node,
            messageId: 'useApiClientForCreate'
          });
        }

        // Detect: axios.get/post/put/delete/patch (not isAxiosError)
        if (
          node.callee.type === 'MemberExpression' &&
          node.callee.object.name === 'axios' &&
          !allowedAxiosUsage.includes(node.callee.property.name)
        ) {
          const methodName = node.callee.property.name;
          if (['get', 'post', 'put', 'delete', 'patch', 'request'].includes(methodName)) {
            context.report({
              node,
              messageId: 'useApiClientForAxios'
            });
          }
        }

        // Detect: fetch('/api/...') - basic pattern for REST API calls
        if (
          node.callee.name === 'fetch' &&
          node.arguments.length > 0 &&
          node.arguments[0].type === 'Literal' &&
          typeof node.arguments[0].value === 'string'
        ) {
          const urlArg = node.arguments[0].value;
          // Only warn for API endpoints, not for static files or external URLs
          if (urlArg.startsWith('/api/') || urlArg.includes('localhost:5245')) {
            context.report({
              node,
              messageId: 'useApiClientForFetch'
            });
          }
        }
      },

      // Detect: api.get/post/put/delete where api is a custom axios instance
      MemberExpression(node) {
        // Look for patterns like: api.get(...) where api is not apiClient
        if (
          node.parent?.type === 'CallExpression' &&
          node.parent.callee === node &&
          node.object?.name === 'api' &&
          ['get', 'post', 'put', 'delete', 'patch'].includes(node.property.name)
        ) {
          // Check if 'api' is defined as a custom axios instance in this file
          const scope = context.sourceCode.getScope(node);
          let foundCustomAxiosInstance = false;
          
          for (let variable of scope.variables) {
            if (variable.name === 'api') {
              // Check if it's defined as axios.create(...)
              variable.defs.forEach(def => {
                if (def.type === 'Variable') {
                  const initText = sourceCode.getText(def.node.init || {});
                  if (initText.includes('axios.create')) {
                    foundCustomAxiosInstance = true;
                  }
                }
              });
            }
          }

          if (foundCustomAxiosInstance) {
            context.report({
              node: node.parent,
              messageId: 'useApiClientForCreate'
            });
          }
        }
      },

      // Detect: fetch with getApiBaseUrl() pattern
      CallExpression(node) {
        if (
          node.callee.name === 'fetch' &&
          node.arguments.length > 0
        ) {
          const firstArg = node.arguments[0];
          // Check for template literal with getApiBaseUrl() call
          if (firstArg.type === 'TemplateLiteral') {
            const hasGetApiBaseUrlCall = firstArg.expressions.some(
              (expr) =>
                expr.type === 'CallExpression' &&
                expr.callee.name === 'getApiBaseUrl'
            );
            if (hasGetApiBaseUrlCall) {
              context.report({
                node,
                messageId: 'useApiClientForFetch'
              });
            }
          }
        }
      }
    };
  }
};
