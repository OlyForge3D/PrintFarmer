// ESLint rule to enforce theme variable usage

export default {
  meta: {
    type: 'suggestion',
    docs: {
      description: 'Disallow hardcoded colors in favor of theme variables',
      category: 'Best Practices',
      recommended: true,
    },
    messages: {
      tailwindColor: 'Use theme variable instead of Tailwind default color "{{color}}". Example: bg-pf-bg-1 instead of bg-gray-800',
      hexColor: 'Use theme variable instead of hex color "{{color}}". Example: var(--pf-success) instead of #3fb950',
      rgbColor: 'Use theme variable instead of rgb/rgba color. Example: var(--pf-text-primary)',
    },
    schema: [
      {
        type: 'object',
        properties: {
          allowedColors: {
            type: 'array',
            items: { type: 'string' },
          },
        },
        additionalProperties: false,
      },
    ],
  },
  create(context) {
        const allowedColors = context.options[0]?.allowedColors || [];
        
        // Tailwind default color pattern
        const tailwindColorPattern = /\b(bg|text|border|ring|divide|placeholder|from|via|to)-(gray|red|green|blue|yellow|indigo|purple|pink|orange|teal|cyan|lime|emerald|sky|violet|fuchsia|rose|amber|slate|zinc|neutral|stone)-(50|100|200|300|400|500|600|700|800|900|950)\b/g;
        
        // Hex color pattern (3 or 6 digits)
        const hexColorPattern = /#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})\b/g;
        
        // RGB/RGBA pattern
        const rgbColorPattern = /rgba?\s*\(/g;

        return {
          JSXAttribute(node) {
            // Check className and style attributes
            if (node.name.name !== 'className' && node.name.name !== 'style') {
              return;
            }

            let value = null;
            
            // Handle string literals
            if (node.value?.type === 'Literal') {
              value = node.value.value;
            }
            // Handle template literals
            else if (node.value?.type === 'JSXExpressionContainer' && 
                     node.value.expression?.type === 'TemplateLiteral') {
              value = node.value.expression.quasis
                .map(q => q.value.cooked)
                .join('');
            }
            // Handle concatenated strings
            else if (node.value?.type === 'JSXExpressionContainer' &&
                     node.value.expression?.type === 'BinaryExpression') {
              // Simple string concatenation check
              const expr = node.value.expression;
              if (expr.operator === '+') {
                value = context.getSourceCode().getText(expr);
              }
            }

            if (!value || typeof value !== 'string') {
              return;
            }

            // Check for Tailwind default colors
            const tailwindMatches = value.matchAll(tailwindColorPattern);
            for (const match of tailwindMatches) {
              const colorClass = match[0];
              if (!allowedColors.includes(colorClass)) {
                context.report({
                  node,
                  messageId: 'tailwindColor',
                  data: { color: colorClass },
                });
              }
            }

            // Check for hex colors
            const hexMatches = value.matchAll(hexColorPattern);
            for (const match of hexMatches) {
              const hexColor = match[0];
              if (!allowedColors.includes(hexColor)) {
                context.report({
                  node,
                  messageId: 'hexColor',
                  data: { color: hexColor },
                });
              }
            }

            // Check for rgb/rgba colors
            if (rgbColorPattern.test(value)) {
              context.report({
                node,
                messageId: 'rgbColor',
              });
            }
          },
        };
      }
    };

