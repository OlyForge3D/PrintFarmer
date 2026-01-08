/**
 * ESLint rule: pf-no-unguarded-console
 * - Warns on unguarded console.log/debug/info in UI code unless surrounded by a PrintFarmerDebug check.
 * - Warns on JSX <pre>{...}</pre> with non-literal expression and JSON.stringify in JSX.
 */
export default {
  meta: {
    type: 'problem',
    docs: {
      description: 'Disallow unguarded console debug logs and raw object JSX dumps',
      recommended: false
    },
    messages: {
      unguardedConsole: 'Use window.PrintFarmerDebug.<area> guard for console.log/debug/info in UI code.',
      rawJsxDump: 'Avoid rendering raw objects in JSX; use renderUnknown(value) or gate behind PrintFarmerDebug.'
    },
    schema: []
  },
  create(context) {
    const sourceCode = context.sourceCode;

    function isConsoleDebug(node) {
      return node && node.type === 'CallExpression' && node.callee && node.callee.type === 'MemberExpression' &&
        node.callee.object && node.callee.object.name === 'console' &&
        node.callee.property && ['log', 'debug', 'info'].includes(node.callee.property.name);
    }

    function hasPrintFarmerGuard(node) {
      // Walk up ancestors until IfStatement found; inspect its test for PrintFarmerDebug member access or typeof window check.
      let current = node.parent;
      while (current) {
        if (current.type === 'IfStatement') {
          const testSrc = sourceCode.getText(current.test);
          if (/PrintFarmerDebug/.test(testSrc)) return true;
          if (/typeof\s+window/.test(testSrc) && /PrintFarmerDebug/.test(testSrc)) return true;
        }
        current = current.parent;
      }
      return false;
    }

    return {
      CallExpression(node) {
        if (isConsoleDebug(node) && !hasPrintFarmerGuard(node)) {
          context.report({ node, messageId: 'unguardedConsole' });
        }
      },
      JSXElement(node) {
        const openingName = node.openingElement && node.openingElement.name && node.openingElement.name.name;
        if (openingName === 'pre') {
          const inner = node.children && node.children.find(c => c.type === 'JSXExpressionContainer');
          if (inner && inner.expression && inner.expression.type !== 'Literal') {
            context.report({ node: inner, messageId: 'rawJsxDump' });
          }
        }

        // Conservative detection for JSON.stringify in JSX
        const text = sourceCode.getText(node);
        if (/JSON\.stringify\s*\(/.test(text)) {
          context.report({ node, messageId: 'rawJsxDump' });
        }
      }
    };
  }
};
