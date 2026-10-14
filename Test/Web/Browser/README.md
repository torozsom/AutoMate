# Browser verification

The existing scripts use Playwright against HTML produced by the real .NET component-rendering tests. They serve
only local fixture HTML/assets on ephemeral loopback ports and use no OAuth, Docker, Azure or LLM calls.

Install the pinned test dependency and browser:

```powershell
npm ci --prefix Test/Web/Browser
cd Test/Web/Browser
npx playwright install chromium
```

Run the rendering tests first, then from the repository root:

```powershell
$env:AUTOMATE_BROWSER_CHANNEL = 'chromium'
npm --prefix Test/Web/Browser test
```

The existing default channel remains Microsoft Edge. CI selects bundled Chromium and installs Linux browser
dependencies, following [Playwright's CI setup](https://playwright.dev/docs/ci). The lockfile formalizes the scripts'
existing dependency; production Web packages are unchanged.

Project section/navigation/focus checks pass. The broader console suite has pre-existing repository-page overflow
at 200% zoom. This was reproduced on original revision `1f35204` in an isolated checkout: GitHub content reached
1,642 px in a 1,440 px viewport. The current revision reproduces the same failure; Local can also overflow.
Repository markup/styles are unchanged. CI keeps section checks blocking and reports this console layout audit as
advisory until a separate visual/accessibility correction is approved. The npm test command still fails on that audit;
no assertions are removed or disabled.
