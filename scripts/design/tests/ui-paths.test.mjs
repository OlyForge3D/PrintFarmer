import test from 'node:test';
import assert from 'node:assert/strict';

import { isUiPath } from '../ui-paths.mjs';

test('scopes React presentation files and the app entrypoint', () => {
  for (const filePath of [
    'src/Web/ReactApp/src/features/new/Panel.tsx',
    'src/Web/ReactApp/src/design-system/themes/dark.css',
    'src/Web/ReactApp/src/features/new/icon.svg',
    'src/Web/ReactApp/index.html',
  ]) {
    assert.equal(isUiPath(filePath), true, filePath);
  }
});

test('scopes SwiftUI views, theme source, and asset catalogs', () => {
  for (const filePath of [
    'mobile/PrintFarmer/Views/Components/ActionButton.swift',
    'mobile/PrintFarmer/Theme/ThemeColors.swift',
    'mobile/PrintFarmer/Assets.xcassets/LaunchAccent.colorset/Contents.json',
    'mobile/PrintFarmer/Assets.xcassets/AppLogo.imageset/logo.svg',
    'mobile/PrintFarmer/ContentView.swift',
    'mobile/PrintFarmer/Navigation/AppDestinationView.swift',
  ]) {
    assert.equal(isUiPath(filePath), true, filePath);
  }
});

test('scopes shared design and reviewer policy artifacts', () => {
  for (const filePath of [
    '.impeccable/config.json',
    '.impeccable/ui-count-ceiling.json',
    'docs/design/ui-source-baseline.json',
    '.github/hooks/impeccable.json',
    '.github/workflows/ci.yml',
    '.github/workflows/design-detector.yml',
    '.github/workflows/squad-review-verdict.yml',
    '.github/copilot-instructions.md',
    '.github/pull_request_template.md',
    '.github/agents/design-reviewer.agent.md',
    '.github/skills/reviewer-protocol/SKILL.md',
    'mobile/.impeccable/design.json',
    'mobile/AGENTS.md',
    'mobile/DESIGN.md',
    '.squad/agents/bishop/charter.md',
    '.squad/agents/dallas/charter.md',
    '.squad/agents/design-reviewer/charter.md',
    '.squad/agents/hicks/charter.md',
    '.squad/agents/vasquez/charter.md',
    '.squad/ceremonies.md',
    '.squad/routing.md',
    '.squad/ralph-instructions.md',
    '.squad/skills/reviewer-protocol/SKILL.md',
    'PRODUCT.md',
    'mobile/PRODUCT.md',
    'scripts/ci/squad-verdict-gate.mjs',
    'scripts/ci/tests/test-design-ci-integration.mjs',
    'scripts/ci/tests/test-squad-verdict-gate.mjs',
    'scripts/ci/verify-squad-verdict.mjs',
    'scripts/design/swiftui-targets.mjs',
  ]) {
    assert.equal(isUiPath(filePath), true, filePath);
  }
});

test('scopes user-visible public identity, app icons, manifest, and printer illustrations', () => {
  for (const filePath of [
    'src/Web/ReactApp/public/printfarmer-logo.svg',
    'src/Web/ReactApp/public/icons/apple-touch-icon.png',
    'src/Web/ReactApp/public/manifest.webmanifest',
    'src/Web/ReactApp/public/assets/printers/generic-printer.svg',
  ]) {
    assert.equal(isUiPath(filePath), true, filePath);
  }
});

test('excludes tests, build output, vendored code, dependencies, and non-UI areas', () => {
  for (const filePath of [
    'src/Web/ReactApp/src/features/__tests__/Panel.test.tsx',
    'src/Web/ReactApp/src/test/fixtures/sample.html',
    'src/Web/ReactApp/src/build/output.css',
    'src/Web/ReactApp/src/vendor/old/Panel.tsx',
    'src/Web/ReactApp/node_modules/example/Panel.tsx',
    'src/Web/ReactApp/e2e/emulator/cameras.spec.ts',
    'mobile/PrintFarmer/Services/PrinterService.swift',
    'mobile/PrintFarmer/ViewModels/DashboardViewModel.swift',
    'mobile/PrintFarmer/Models/FarmShape.swift',
    'mobile/PrintFarmer/Services/PrinterViewService.swift',
    'src/Web/ReactApp/public/assets/orcaslicer/creality/Printer.png',
    'src/Web/ReactApp/public/icons/orca/param_dependencies_printers.svg',
    'src/Web/ReactApp/public/assets/printers/bed-model.stl',
    'src/Web/ReactApp/public/assets/hdri/studio_small_03_1k.hdr',
    'src/Web/ReactApp/public/licenses/droid-fonts/README.txt',
    'src/Web/ReactApp/public/sw.js',
    'src/Web/ReactApp/public/images/unclassified.png',
  ]) {
    assert.equal(isUiPath(filePath), false, filePath);
  }
});

test('normalizes Windows separators', () => {
  assert.equal(isUiPath('mobile\\PrintFarmer\\Views\\PrinterDetailView.swift'), true);
});
