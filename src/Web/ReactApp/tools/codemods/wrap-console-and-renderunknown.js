/**
 * jscodeshift codemod: wrap-console-and-renderunknown
 *
 * - Wraps console.log/info/debug calls in a PrintFarmerDebug guard based on file name.
 *   Example: console.debug('x') -> if (win.PrintFarmerDebug?.printerSignalR) { console.debug('x') }
 *
 * - Replaces common raw JSX dumps like {JSON.stringify(obj)} or <pre>{JSON.stringify(obj, null, 2)}</pre>
 *   with renderUnknown(obj) (ensures import exists is left to the developer).
 *
 * Usage (dry-run):
 *   npx jscodeshift -t ./tools/codemods/wrap-console-and-renderunknown.js src/Web/ReactApp/src --extensions=ts,tsx --parser=tsx --dry
 * Apply:
 *   npx jscodeshift -t ./tools/codemods/wrap-console-and-renderunknown.js src/Web/ReactApp/src --extensions=ts,tsx --parser=tsx
 */

module.exports = function(fileInfo, api, options) {
  const j = api.jscodeshift;
  const root = j(fileInfo.source);
  const path = fileInfo.path || fileInfo.filePath || fileInfo.filename || '';

  // Safety: skip files that implement or export renderUnknown or whose path contains renderUnknown
  const lowerPath = path.toLowerCase();
  if (lowerPath.includes('renderunknown')) {
    return fileInfo.source;
  }
  const definesRenderUnknown = root.find(j.FunctionDeclaration, { id: { name: 'renderUnknown' } }).size() > 0
    || root.find(j.VariableDeclarator, { id: { name: 'renderUnknown' } }).size() > 0
    || root.find(j.ExportNamedDeclaration, { declaration: { type: 'FunctionDeclaration', id: { name: 'renderUnknown' } } }).size() > 0;
  if (definesRenderUnknown) {
    return fileInfo.source;
  }

  // Infer an area name from the filename, e.g. printer-signalr.ts -> printerSignalR
  function inferAreaName(filePath) {
    const fname = filePath.split('/').pop() || filePath;
    const base = fname.replace(/\.(tsx|ts|jsx|js)$/, '');
    // convert kebab/case to camelCase and suffix with capitalized words where appropriate
    const parts = base.split(/[^a-zA-Z0-9]+/).filter(Boolean);
    if (parts.length === 0) return 'global';
    // handle trailing 'signalr' -> SignalR
    const camel = parts.map((p, i) => i === 0 ? p.toLowerCase() : p.charAt(0).toUpperCase() + p.slice(1)).join('');
    // make signalr -> SignalR
    return camel.replace(/Signalr|signalr|SignalR/gi, 'SignalR');
  }

  // Mapping table: add known filename -> areaName entries here for predictable guards
  const filenameAreaMap = {
    'printer-signalr.ts': 'printerSignalR',
    'harvest-signalr.ts': 'harvestSignalR',
    'setupwizard.tsx': 'setupWizard',
    'user-management-page.tsx': 'userManagementPage',
    'filebrowser.tsx': 'fileBrowser',
    'printercard.tsx': 'printerCard',
    // add more mappings as needed
  };

  const baseName = (path.split('/').pop() || '').toLowerCase();
  const areaName = filenameAreaMap[baseName] || inferAreaName(path);
  let usedRenderUnknown = false;

  // 1) Wrap console.debug/info/log calls with guard
  root.find(j.CallExpression, {
    callee: {
      type: 'MemberExpression',
      object: { name: 'console' },
      property: (prop) => ['debug','info','log'].includes(prop.name || prop.value)
    }
  }).forEach(p => {
    const callExpr = p.node;
    // Only transform direct console.*(...) expressions (not assignments or other parent types)
    const parent = p.parent.node;
    // If already inside an IfStatement checking PrintFarmerDebug, skip
    let ancestor = p.parent;
    let alreadyGuarded = false;
    while (ancestor) {
      if (ancestor.node && ancestor.node.type === 'IfStatement') {
        try {
          const testSrc = j(ancestor.node.test).toSource();
          if (testSrc.includes('PrintFarmerDebug')) {
            alreadyGuarded = true;
            break;
          }
        } catch (e) {}
      }
      ancestor = ancestor.parent;
    }
    if (alreadyGuarded) return;

    // Replace the expression statement that contains the call with an if guard
    const exprStmt = j(callExpr).closest(j.ExpressionStatement);
    if (exprStmt.length === 0) return;

    // Build guard: window.PrintFarmerDebug?.<areaName>
    const pf = j.optionalMemberExpression(j.memberExpression(j.identifier('window'), j.identifier('PrintFarmerDebug')), j.identifier(areaName), false, true);
    const guardTest = pf; // optionalMemberExpression covers the existence checks

    const guard = j.ifStatement(guardTest, j.blockStatement([ j.expressionStatement(callExpr) ]));

    exprStmt.replaceWith(guard);
  });

  // 2) Replace JSON.stringify() inside JSX expressions with renderUnknown(arg)
  // Patterns handled:
  //   {JSON.stringify(x)}
  //   <pre>{JSON.stringify(x, null, 2)}</pre>

  root.find(j.JSXExpressionContainer, {
    expression: {
      type: 'CallExpression',
      callee: { object: { name: 'JSON' }, property: { name: 'stringify' } }
    }
  }).forEach(p => {
    const call = p.node.expression;
    // take the first arg of JSON.stringify(...) as the subject
    const firstArg = call.arguments && call.arguments[0];
    if (!firstArg) return;
    // replace with renderUnknown(firstArg)
    const newExpr = j.callExpression(j.identifier('renderUnknown'), [ firstArg ]);
    usedRenderUnknown = true;
    j(p).replaceWith(j.jsxExpressionContainer(newExpr));
  });

  // 3) Replace literal usage of JSON.stringify(...) used outside JSX but as children in React.createElement
  root.find(j.CallExpression, {
    callee: {
      type: 'MemberExpression',
      object: { name: 'JSON' },
      property: { name: 'stringify' }
    }
  }).forEach(p => {
    // Avoid replacing the ones already handled in JSX
    if (j(p).closest(j.JSXExpressionContainer).length > 0) return;

    // If the parent is an ExpressionStatement by itself (console.log(JSON.stringify(...))) -> leave it (console guards will wrap)
    // If it's used as a return value in JSX or assigned to variable to render, skip for now.
  });

  // If we used renderUnknown, ensure import exists
  if (usedRenderUnknown) {
    const hasImport = root.find(j.ImportDeclaration, { source: { value: '@/utils/renderUnknown' } }).size() > 0
      || root.find(j.ImportDeclaration, { source: { value: "@/utils/renderUnknown" } }).size() > 0;
    if (!hasImport) {
      // insert at top after existing imports
      const imports = root.find(j.ImportDeclaration);
      const spec = j.importDeclaration([
        j.importSpecifier(j.identifier('renderUnknown'))
      ], j.literal('@/utils/renderUnknown'));
      if (imports.size() > 0) {
        imports.at(imports.size()-1).insertAfter(spec);
      } else {
        root.get().node.program.body.unshift(spec);
      }
    }
  }

  return root.toSource({ quote: 'single' });
};
