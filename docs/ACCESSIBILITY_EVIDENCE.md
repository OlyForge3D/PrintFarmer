# Accessibility Evidence

This note records the bounded follow-up for issue #3734. The checks below are
representative evidence, not a Cartesian product of every route, theme, browser,
viewport, assistive technology, or interaction state.

## Automated browser evidence

Run from `src/Web/ReactApp/`:

```text
npm run typecheck:e2e
npx playwright test --config=e2e/marketing/playwright.config.ts accessibility-follow-up.spec.ts
npm run test:ui-coverage:candidate
```

`e2e/marketing/accessibility-follow-up.spec.ts` covers:

- `/dashboard`, `/printers`, and the user appearance settings route;
- normal motion versus `prefers-reduced-motion: reduce`, including the
  app's `--motion-safe` state;
- representative theme/contrast pairs: dark/normal on dashboard,
  light/normal on printers, and forge/high on appearance settings;
- route rendering, root theme/contrast state, horizontal overflow, and the
  existing synthetic-network isolation.

The maintained `test:ui-coverage:candidate` command runs this spec together
with the catalog-driven UI coverage candidates, so required UI-coverage CI
protects these four checks.

The existing UI coverage suite still exercises 200%-equivalent CSS reflow,
high-contrast scenes, axe checks, keyboard traversal, and narrow viewports.
Those are not browser-zoom or screen-reader certification.

## Manual 200% browser zoom

Use a headed Chromium or Edge window at the normal desktop viewport, then use
the browser's own zoom control (not CSS `zoom`, root font-size changes, or a
narrow viewport proxy) until the browser reports **200%**. Check these routes:

| Route | Check |
| --- | --- |
| `/dashboard` | Main heading, navigation, cards, and primary actions remain reachable; no content is lost horizontally. |
| `/printers` | Printer cards and card actions remain readable and operable. |
| `/settings?scope=user&tab=profile&sub=appearance` | Settings navigation, form controls, and save actions remain reachable. |

Record browser, OS, route, zoom indicator, viewport, theme/contrast, and any
horizontal scrolling or clipped content. A 640px CSS viewport, a 720px
equivalent viewport, `deviceScaleFactor`, `visualViewport.scale`, axe output,
or a CSS `zoom: 2` fixture must not be reported as this check.

Manual observation completed on 2026-11-16: Microsoft Edge's native menu
reported **Zoom 200%** on Windows for both the local setup wizard
(`http://127.0.0.1:3002/login`) and the synthetic marketing surface
(`http://127.0.0.1:4179/PrintFarmer/`). On the setup wizard, the heading, step
navigation, labels, and form controls were visibly enlarged and remained
reachable through the document scroll area. On the marketing surface, the
navigation, hero heading, calls to action, and synthetic fleet section remained
present in the accessibility tree at 200%. No setup secret, account data, or
credentials were entered. These are unauthenticated surfaces; authenticated
dashboard, printers, and settings routes could not be manually exercised in
this pass. The automated isolated route checks do not substitute for that
limitation.

## Manual assistive-technology checks and limitations

The following checks are intentionally manual because Playwright and axe expose
DOM/accessibility-tree assertions, not a screen-reader user's speech output:

- With NVDA or Narrator on Windows, read the page title and landmarks on
  `/dashboard`, move through the main navigation, and activate one primary
  action from the keyboard.
- On `/printers`, verify a printer card's accessible name and the purpose/state
  of one card action.
- On appearance settings, verify the selected theme and contrast controls
  announce their labels and selected state.
- Open one dialog or sidebar, verify focus enters it, Escape closes it, and
  focus returns to the trigger.

This repository does not run NVDA, Narrator, JAWS, VoiceOver, or TalkBack in
CI. Therefore automated axe results, accessibility-tree snapshots, ARIA
assertions, and keyboard checks remain supporting evidence only; they do not
certify screen-reader output, speech verbosity, braille output, or
screen-reader-specific navigation modes. Record unavailable AT/tooling rather
than inferring a pass.

## Evidence limits

The selected route/theme matrix is a sample chosen to cross the shared shell,
printer content, and settings controls. It does not claim every route × theme ×
contrast × motion × viewport combination is covered. Native iOS accessibility
and simulator evidence are outside this web follow-up.
