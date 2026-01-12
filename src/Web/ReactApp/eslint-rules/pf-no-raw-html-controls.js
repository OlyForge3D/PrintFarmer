/**
 * ESLint rule: pf-no-raw-html-controls
 * 
 * Enforces use of shared UI components instead of raw HTML form controls.
 * - Warns on raw <button> elements (use Button component)
 * - Warns on raw <input> elements (use Input component or FileUpload)
 * - Warns on raw <select> elements (use Select component)
 * - Warns on raw <textarea> elements (use Textarea component)
 * - Warns on raw <input type="checkbox"> (use Checkbox component)
 * - Warns on raw <input type="radio"> (use Radio component)
 * - Warns on raw <input type="file"> (use FileUpload component)
 * 
 * Exceptions:
 * - Hidden inputs (type="hidden")
 * - Inputs with data-* or aria-* attributes indicating special handling
 * - Comments with @component-override directive
 */

export default {
  meta: {
    type: 'suggestion',
    docs: {
      description: 'Enforce use of shared UI components instead of raw HTML form controls',
      recommended: true,
      url: 'file:///src/Web/ReactApp/UI_COMPONENTS_GUIDE.md'
    },
    messages: {
      useButton: 'Use <Button> component from @/components/ui instead of raw <button>. See UI_COMPONENTS_GUIDE.md for patterns.',
      useInput: 'Use <Input> component wrapped in <FormField> instead of raw <input>. See UI_COMPONENTS_GUIDE.md for patterns.',
      useCheckbox: 'Use <Checkbox> component from @/components/ui instead of raw <input type="checkbox">.',
      useRadio: 'Use <Radio> component from @/components/ui instead of raw <input type="radio">.',
      useFileUpload: 'Use <FileUpload> component from @/components/ui instead of raw <input type="file">.',
      useSelect: 'Use <Select> component wrapped in <FormField> instead of raw <select>.',
      useTextarea: 'Use <Textarea> component wrapped in <FormField> instead of raw <textarea>.',
    },
    schema: []
  },

  create(context) {
    const sourceCode = context.sourceCode;

    function hasComponentOverrideComment(node) {
      // Check for @component-override comment above the element
      if (!sourceCode.getCommentsBefore) return false;
      
      const comments = sourceCode.getCommentsBefore(node);
      if (!comments) return false;
      
      for (const comment of comments) {
        if (/@component-override/.test(comment.value)) {
          return true;
        }
      }
      return false;
    }

    function isHiddenInput(node) {
      if (node.name.name !== 'input') return false;
      const typeAttr = node.attributes?.find(attr => 
        attr.type === 'JSXAttribute' && attr.name.name === 'type'
      );
      return typeAttr?.value?.value === 'hidden';
    }

    function hasSpecialAttribute(node) {
      // Check for data-* or aria-* attributes
      return node.attributes?.some(attr => 
        attr.type === 'JSXAttribute' && (
          attr.name.name?.startsWith('data-') ||
          attr.name.name?.startsWith('aria-')
        )
      );
    }

    function getInputType(node) {
      const typeAttr = node.attributes?.find(attr => 
        attr.type === 'JSXAttribute' && attr.name.name === 'type'
      );
      return typeAttr?.value?.value || 'text';
    }

    function isRawControl(node) {
      // Check if it's inside a component that imports shared UI
      // This is a heuristic - we check if the file imports UI components
      const sourceText = sourceCode.getText();
      return /import\s+{.*Button.*}\s+from\s+['"]@\/components\/ui['"]/.test(sourceText) ||
             /import\s+{.*Input.*}\s+from\s+['"]@\/components\/ui['"]/.test(sourceText);
    }

    return {
      JSXOpeningElement(node) {
        // Skip if @component-override comment present
        if (hasComponentOverrideComment(node)) {
          return;
        }

        // Check for raw <button>
        if (node.name.name === 'button') {
          context.report({
            node,
            messageId: 'useButton'
          });
        }

        // Check for raw <input>
        if (node.name.name === 'input') {
          // Skip hidden inputs
          if (isHiddenInput(node)) {
            return;
          }

          const type = getInputType(node);

          if (type === 'checkbox') {
            context.report({
              node,
              messageId: 'useCheckbox'
            });
          } else if (type === 'radio') {
            context.report({
              node,
              messageId: 'useRadio'
            });
          } else if (type === 'file') {
            context.report({
              node,
              messageId: 'useFileUpload'
            });
          } else if (type === 'text' || type === 'email' || type === 'password' || type === 'number' || type === 'search' || type === 'url' || type === 'tel' || !type) {
            // Only report if file imports UI components (indicates this is a pages/components file that should use them)
            if (isRawControl(node)) {
              context.report({
                node,
                messageId: 'useInput'
              });
            }
          }
        }

        // Check for raw <select>
        if (node.name.name === 'select') {
          context.report({
            node,
            messageId: 'useSelect'
          });
        }

        // Check for raw <textarea>
        if (node.name.name === 'textarea') {
          context.report({
            node,
            messageId: 'useTextarea'
          });
        }
      }
    };
  }
};
