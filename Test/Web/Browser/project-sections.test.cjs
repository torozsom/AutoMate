// Runs against provider-free HTML rendered by TelemetryPageRenderingTests.
const fs = require('fs');
const path = require('path');
const http = require('http');
const assert = require('node:assert/strict');
const {chromium} = require(process.env.AUTOMATE_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../..');
const preview = path.join(root, '.artifacts/metrics-preview');
const assets = path.join(root, 'Web/wwwroot');
const server = http.createServer(
    /**
     * Serves provider-free project/history previews and local assets within the fixture roots.
     * @param {import("node:http").IncomingMessage} req - Loopback fixture resource request.
     * @param {import("node:http").ServerResponse} res - Loopback fixture response receiving the file or fixed status.
     * @returns {void} No value; missing or escaped paths receive 404.
     */
    (req, res) => {
        const pathname = new URL(req.url, 'http://localhost').pathname;
        if (pathname === '/favicon.ico') {
            res.writeHead(204);
            res.end();
            return;
        }
        const relative = pathname.replace(/^\//, '');
        const base = /^(details|details-analysis|history|project)\.html$|^Web.styles.css$|^bootstrap.css$/.test(relative) ? preview : assets;
        const file = path.resolve(base, relative);
        if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
            res.writeHead(404);
            res.end();
            return;
        }
        res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
        fs.createReadStream(file).pipe(res);
    });
(
    /**
     * Checks fragment navigation, analysis-dialog focus, responsive section order and both themes.
     * @returns {Promise<void>} Resolves after assertions and browser/server cleanup; rejects on failure.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    async () => {
        await new Promise(
            /**
             * Starts the fixture HTTP server on an ephemeral loopback port.
             * @param {function(): void} resolve - Promise completion callback passed to the server listener.
             * @returns {import("node:http").Server} The listening fixture server; completion uses the supplied callback.
             */
            resolve => server.listen(0, '127.0.0.1', resolve));
        const browser = await chromium.launch({
            headless: true,
            channel: process.env.AUTOMATE_BROWSER_CHANNEL || 'msedge'
        });
        try {
            const address = 'http://127.0.0.1:' + server.address().port;
            const page = await browser.newPage({viewport: {width: 1280, height: 900}, reducedMotion: 'reduce'});
            page.setDefaultTimeout(5000);
            const errors = [];
            page.on('pageerror',
                /**
                 * Records browser-script failures for the final assertion.
                 * @param {Error} e - Browser or suite failure to record.
                 * @returns {number} Updated captured-error count.
                 */
                e => errors.push(e.message));
            page.on('pageerror',
                /**
                 * Prints a page-script failure for fixture diagnostics.
                 * @param {Error} e - Browser or suite failure to record.
                 * @returns {void} No value.
                 */
                e => console.error('Browser script error:', e.message));
            page.on('console',
                /**
                 * Prints browser console errors for fixture diagnostics.
                 * @param {import("playwright").ConsoleMessage} message - Browser console message captured by the fixture.
                 * @returns {void} No value; other console levels are ignored.
                 */
                message => {
                    if (message.type() === 'error') console.error(message.text());
                });
            page.on('requestfailed',
                /**
                 * Prints a failed fixture-resource request for diagnostics.
                 * @param {import("playwright").Request} request - Failed browser resource request.
                 * @returns {void} No value.
                 */
                request => console.error('Failed resource:', request.url(), request.failure()?.errorText));
            await page.goto(address + '/details.html#configuration');
            await page.waitForFunction(
                /**
                 * Waits for direct fragment navigation to focus the configuration heading.
                 * @returns {boolean} True when the heading owns focus.
                 */
                () => document.activeElement?.id === 'configuration-title');
            assert.equal(await page.locator('.project-section-tabs a').count(), 6);
            assert.equal(await page.locator('#ai-analysis').count(), 0);
            await page.locator('button.terminal-ai-shortcut').click();
            await page.waitForFunction(
                /**
                 * Waits for analysis-dialog focus after opening its terminal shortcut.
                 * @returns {boolean|undefined} Whether the dialog contains focus, or undefined before it exists.
                 */
                () => document.querySelector('[role=dialog]')?.contains(document.activeElement));
            assert.equal(await page.locator('.assessment-context select').count(), 2);
            assert.equal(await page.locator('.assessment-context select').first().locator('option').count(), 5);
            assert.equal(await page.locator('.assessment-context select').last().locator('option').count(), 6);
            assert.ok(await page.locator('.assessment-context').innerText().then(
                /**
                 * Checks that selected-context text includes the Azure console source.
                 * @param {string} text - Rendered label or context text to inspect.
                 * @returns {boolean} True when the expected source label is present.
                 */
                text => text.includes('Azure application console output')));
            await page.keyboard.press('Escape');
            await page.waitForFunction(
                /**
                 * Waits for Escape dismissal and shortcut focus restoration.
                 * @returns {boolean|undefined} Whether the dialog is absent and the shortcut owns focus.
                 */
                () => !document.querySelector('[role=dialog]') && document.activeElement?.classList.contains('terminal-ai-shortcut'));
            await page.locator('.project-section-tabs a[href="#logs"]').click();
            await page.waitForFunction(
                /**
                 * Waits for the live-log section heading to receive focus.
                 * @returns {boolean} True when the logs heading owns focus.
                 */
                () => document.activeElement?.id === 'logs-title');
            await page.goBack();
            await page.waitForFunction(
                /**
                 * Waits for back navigation to restore the prior configuration fragment.
                 * @returns {boolean} True when the expected fragment is current.
                 */
                () => location.hash === '#configuration');
            // Keyboard activation uses the same real anchors and moves focus to the section heading.
            await page.locator('.project-section-tabs a[href="#overview"]').focus();
            await page.keyboard.press('Enter');
            await page.waitForFunction(
                /**
                 * Waits for keyboard activation to focus the overview heading.
                 * @returns {boolean} True when the overview heading owns focus.
                 */
                () => document.activeElement?.id === 'overview-title');
            await page.screenshot({path: path.join(preview, 'details-light.png'), fullPage: true});
            await page.evaluate(
                /**
                 * Applies dark theme for responsive project-navigation checks.
                 * @returns {string} The assigned theme identifier.
                 */
                () => document.documentElement.dataset.bsTheme = 'dark');
            await page.screenshot({path: path.join(preview, 'details-dark.png'), fullPage: true});
            await page.setViewportSize({width: 390, height: 844});
            await page.goto(address + '/details.html#overview');
            assert.ok(await page.locator('.project-section-tabs').evaluate(
                /**
                 * Checks that mobile section tabs use their own horizontal overflow.
                 * @param {Element} nav - Rendered fixture element to inspect.
                 * @returns {boolean} True when the tab content exceeds its viewport.
                 */
                nav => nav.scrollWidth > nav.clientWidth));
            assert.ok(await page.evaluate(
                /**
                 * Checks that mobile content does not overflow the page viewport.
                 * @returns {boolean} True when document width stays within one pixel of the viewport.
                 */
                () => document.documentElement.scrollWidth <= innerWidth + 1), 'No page-wide mobile overflow');
            assert.ok(await page.locator('.terminal-ai-shortcut').evaluate(
                /**
                 * Checks that the terminal analysis shortcut remains fully visible on mobile.
                 * @param {Element} link - Rendered fixture element to inspect.
                 * @returns {boolean} True when its left and right edges stay in the viewport.
                 */
                link => {
                    const box = link.getBoundingClientRect();
                    return box.left >= 0 && box.right <= innerWidth;
                }), 'AI shortcut remains visible on mobile');
            await page.screenshot({path: path.join(preview, 'details-mobile.png'), fullPage: true});
            await page.goto(address + '/history.html');
            assert.equal(await page.locator('.assessment-context').count(), 1);
            assert.ok(await page.locator('#history-logs-title').evaluate(
                /**
                 * Checks that saved logs precede saved metrics in the history layout.
                 * @param {Element} logs - Rendered fixture element to inspect.
                 * @returns {boolean} True when the logs heading is vertically above the metrics heading.
                 */
                logs =>
                    logs.getBoundingClientRect().top < document.querySelector('#history-metrics-title').getBoundingClientRect().top));
            await page.screenshot({path: path.join(preview, 'history-mobile.png'), fullPage: true});
            await page.setViewportSize({width: 1280, height: 900});
            await page.screenshot({path: path.join(preview, 'history-light.png'), fullPage: true});
            await page.evaluate(
                /**
                 * Applies dark theme before the history screenshot.
                 * @returns {string} The assigned theme identifier.
                 */
                () => document.documentElement.dataset.bsTheme = 'dark');
            await page.screenshot({path: path.join(preview, 'history-dark.png'), fullPage: true});
            assert.deepEqual(errors, []);
            console.log('Browser checks passed: direct fragments, anchors, focus, back navigation, reduced motion, mobile overflow, section order and both themes.');
        } finally {
            await browser.close();
            server.close();
        }
    })().catch(
    /**
     * Reports a browser-suite failure, closes the fixture server and marks the process unsuccessful.
     * @param {Error} e - Browser or suite failure to record.
     * @returns {void} No value.
     */
    e => {
        console.error(e);
        server.close();
        process.exitCode = 1;
    });
