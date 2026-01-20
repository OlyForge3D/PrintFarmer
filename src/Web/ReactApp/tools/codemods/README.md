wrap-console-and-renderunknown codemod

This directory contains a jscodeshift codemod to help automate wrapping noisy console.* calls
and replacing raw JSX JSON dumps with a safer `renderUnknown(...)` render helper.

- Prerequisites
- Node.js >=24.0 (required for this project)
- Install jscodeshift globally or use npx (recommended)

Dry-run (recommended):
```bash
cd src/Web/ReactApp
npx jscodeshift -t ./tools/codemods/wrap-console-and-renderunknown.js src --extensions=ts,tsx --parser=tsx --dry
```

Apply:
```bash
cd src/Web/ReactApp
npx jscodeshift -t ./tools/codemods/wrap-console-and-renderunknown.js src --extensions=ts,tsx --parser=tsx
```

Notes and limitations
- The codemod infers an area name from the filename (e.g. `printer-signalr.ts` -> `printerSignalR`).
  If the inference is wrong, review changes before committing.
- The transform only wraps top-level console.debug/info/log expression statements. It doesn't
  automatically insert `renderUnknown` imports — add `import { renderUnknown } from '@/utils/renderUnknown';`
  when needed.
- The codemod is intentionally conservative. Run the dry-run first and inspect diffs.
- Complex JSON.stringify uses (e.g., stored in variables or passed through formatting) may not be
  replaced. The tool focuses on common patterns like `{JSON.stringify(obj)}` inside JSX and
  direct `console.debug(JSON.stringify(obj))` style calls.

TypeScript helper & filename mapping
- The codemod no longer injects a local `win` variable. Instead it uses `window.PrintFarmerDebug?.<area>`.
- A TypeScript declaration file has been added at `src/types/printfarmer-debug.d.ts`. It declares `window.PrintFarmerDebug` and lets you add area flags to avoid TS errors.
- You can customize the codemod's filename->area mapping inside `wrap-console-and-renderunknown.js` by editing the `filenameAreaMap` object near the top of the file.

Suggested workflow
1. Run dry-run across `src` and inspect diffs.
2. Pick a small batch of files and run the transform (or run with --dry and then apply).
3. Run `npm run lint` and `npm test` immediately after each batch.
4. Commit small, reviewed diffs.
