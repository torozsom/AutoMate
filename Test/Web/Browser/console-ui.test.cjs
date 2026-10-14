// Real component HTML from ConsoleRenderingTests and TelemetryPageRenderingTests; no OAuth or provider calls.
const fs = require('fs'), path = require('path'), http = require('http'), assert = require('node:assert/strict');
const {chromium} = require(process.env.AUTOMATE_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../../..'), preview = path.join(root, '.artifacts/console-preview'),
    metrics = path.join(root, '.artifacts/metrics-preview'), assets = path.join(root, 'Web/wwwroot');
const server = http.createServer(
    /**
     * Serves provider-free component previews and local assets within the selected fixture root.
     * @param {import("node:http").IncomingMessage} req - Loopback fixture resource request.
     * @param {import("node:http").ServerResponse} res - Loopback fixture response receiving the file or fixed status.
     * @returns {void} No value; missing or escaped paths receive 404.
     */
    (req, res) => {
        const name = new URL(req.url, 'http://localhost').pathname.replace(/^\//, '');
        if (name === 'favicon.ico') {
            res.writeHead(204);
            res.end();
            return;
        }
        const base = /^(details|details-analysis|history)\.html$/.test(name) ? metrics : /^[a-z-]+\.html$|^(bootstrap.css|Web.styles.css)$/.test(name) ? preview : assets;
        const file = path.resolve(base, name);
        if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
            res.writeHead(404);
            res.end();
            return;
        }
        res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : /\.(ttf|woff2?)$/.test(file) ? 'font/' + path.extname(file).slice(1) : 'text/html');
        fs.createReadStream(file).pipe(res);
    });
(
    /**
     * Checks preview themes, viewports, contrast, dialogs, terminal dimensions and keyboard navigation.
     * @returns {Promise<void>} Resolves after assertions and browser/server cleanup; rejects on failure.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    async () => {
        await new Promise(
            /**
             * Starts the fixture HTTP server on an ephemeral loopback port.
             * @param {function(): void} resolve - Promise completion callback passed to the server listener.
             * @returns {import("node:http").Server} The listening fixture server; Promise completion uses the supplied callback.
             */
            resolve => server.listen(0, '127.0.0.1', resolve));
        const browser = await chromium.launch({
            headless: true,
            channel: process.env.AUTOMATE_BROWSER_CHANNEL || 'msedge'
        });
        const errors = [];
        let checked = 0;
        try {
            const page = await browser.newPage({reducedMotion: 'reduce'});
            page.on('pageerror',
                /**
                 * Records page-script failures for the final browser assertion.
                 * @param {Error} e - Browser or suite failure to record.
                 * @returns {number} The updated captured-error count.
                 */
                e => errors.push(e.message));
            const url = 'http://127.0.0.1:' + server.address().port;
            for (const name of ['overview', 'projects', 'azure', 'github', 'local', 'landing', 'login', 'register', 'verify', 'configuration', 'details', 'history', 'overview-saas', 'projects-saas', 'azure-saas']) {
                for (const theme of ['light', 'dark']) {
                    for (const width of [360, 768, 1440]) {
                        await page.setViewportSize({width, height: 960});
                        await page.goto(url + '/' + name + '.html');
                        await page.evaluate(
                            /**
                             * Applies the selected theme inside the preview document.
                             * @param {string} theme - Light or dark fixture theme.
                             * @returns {string} The assigned theme identifier.
                             */
                            theme => document.documentElement.dataset.bsTheme = theme, theme);
                        await page.evaluate(
                            /**
                             * Waits for preview fonts to finish loading.
                             * @returns {Promise<FontFaceSet>} Resolves when document font loading completes.
                             */
                            () => document.fonts.ready);
                        assert.ok(await page.evaluate(
                            /**
                             * Checks that both locally served reference fonts are available.
                             * @returns {boolean} True when Inter and JetBrains Mono both load.
                             */
                            () => document.fonts.check('12px Inter') && document.fonts.check('12px "JetBrains Mono"')), 'Reference fonts must load locally');
                        const overflow = await page.evaluate(
                            /**
                             * Captures viewport width and a bounded list of overflowing elements.
                             * @returns {object} Document width, viewport width and at most eight overflowing element labels.
                             */
                            () => ({
                                width: document.documentElement.scrollWidth, viewport: innerWidth,
                                elements: [...document.querySelectorAll('body *')].filter(
                                    /**
                                     * Finds elements extending beyond the right viewport boundary.
                                     * @param {Element} e - Rendered element to filter, format or inspect.
                                     * @returns {boolean} True when the element exceeds the viewport by more than one pixel.
                                     */
                                    e => e.getBoundingClientRect().right > innerWidth + 1)
                                    .slice(0, 8).map(
                                        /**
                                         * Formats an overflowing element for fixture failure diagnostics.
                                         * @param {Element} e - Rendered element to filter, format or inspect.
                                         * @returns {string} Element tag and class names.
                                         */
                                        e => e.tagName + '.' + e.className)
                            }));
                        assert.ok(overflow.width <= overflow.viewport + 1, name + ' ' + theme + ' ' + width + ' must not overflow: ' + JSON.stringify(overflow));
                        await page.screenshot({
                            path: path.join(preview, name + '-' + theme + '-' + width + '.png'),
                            fullPage: true
                        });
                        if (theme === 'dark' && width === 1440 && ['overview', 'details'].includes(name)) {
                            await page.screenshot({path: path.join(preview, name + '-desktop.png')});
                        }
                        checked++;
                        const lowContrast = await page.evaluate(
                            /**
                             * Finds visible enabled reference text below the required contrast ratio.
                             * @returns {string[]} Bounded text excerpts from elements below 4.5:1 contrast.
                             */
                            () => {
                                /**
                                 * Parses CSS RGB or sRGB text into three channels on the 0–255 scale.
                                 * @param {string} value - Computed CSS color representation.
                                 * @returns {number[]} Red, green and blue channel values.
                                 */
                                const rgb = value => (value.match(/[0-9.]+/g) || []).slice(0, 3).map(
                                    /**
                                     * Converts a parsed RGB or normalized sRGB channel to the 0–255 scale.
                                     * @param {string} v - Color-channel value to convert or accumulate.
                                     * @returns {number} Numeric color channel.
                                     */
                                    v => Number(v) * (value.startsWith('color(srgb') ? 255 : 1));
                                /**
                                 * Computes relative luminance from the parsed CSS color channels.
                                 * @param {string} value - Computed CSS color representation.
                                 * @returns {number} Weighted linear relative luminance.
                                 */
                                const luminance = value => rgb(value).map(
                                    /**
                                     * Linearizes a normalized color channel for relative-luminance calculation.
                                     * @param {number} v - Color-channel value to convert or accumulate.
                                     * @returns {number} Linear color-channel contribution.
                                     */
                                    v => {
                                        v /= 255;
                                        return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
                                    }).reduce(
                                    /**
                                     * Accumulates the weighted red, green and blue luminance contributions.
                                     * @param {number} sum - Accumulated relative luminance.
                                     * @param {number} v - Color-channel value to convert or accumulate.
                                     * @param {number} i - Color-channel position.
                                     * @returns {number} Updated relative-luminance total.
                                     */
                                    (sum, v, i) => sum + v * [.2126, .7152, .0722][i], 0);
                                /**
                                 * Finds the nearest nontransparent computed background, with the body as fallback.
                                 * @param {Element} element - Rendered fixture element to inspect.
                                 * @returns {string} Computed background color used by contrast assertions.
                                 */
                                const background = element => {
                                    for (let e = element; e; e = e.parentElement) {
                                        const c = getComputedStyle(e).backgroundColor;
                                        if (c !== 'rgba(0, 0, 0, 0)' && c !== 'transparent') return c;
                                    }
                                    return getComputedStyle(document.body).backgroundColor;
                                };
                                return [...document.querySelectorAll('.console-kpi span,.console-meta,.console-subtext,.page-description,.btn-primary,.detail-list dt,.detail-list dd,.nav-section-label,.skip-link')]
                                    .filter(
                                        /**
                                         * Includes only rendered enabled elements in contrast checking.
                                         * @param {Element} e - Rendered element to filter, format or inspect.
                                         * @returns {boolean} True when the element is visible and enabled.
                                         */
                                        e => e.getClientRects().length && !e.matches(':disabled')).filter(
                                        /**
                                         * Checks the computed foreground/background contrast against 4.5:1.
                                         * @param {Element} e - Rendered element to filter, format or inspect.
                                         * @returns {boolean} True when the contrast ratio is below the required threshold.
                                         */
                                        e => {
                                            const style = getComputedStyle(e);
                                            const a = luminance(style.color), b = luminance(background(e));
                                            return (Math.max(a, b) + .05) / (Math.min(a, b) + .05) < 4.5;
                                        }).map(
                                        /**
                                         * Bounds a low-contrast text excerpt for assertion diagnostics.
                                         * @param {Element} e - Rendered element to filter, format or inspect.
                                         * @returns {string} At most 80 characters of element text.
                                         */
                                        e => e.textContent.slice(0, 80));
                            });
                        assert.deepEqual(lowContrast, [], name + ' ' + theme + ' text contrast');
                        if (name.startsWith('projects')) {
                            assert.equal(await page.locator('.project-inventory tbody tr').count(), 3);
                            for (const row of await page.locator('.project-inventory tbody tr').all()) {
                                assert.deepEqual((await row.locator('.console-row-actions button').allTextContents()).map(
                                    /**
                                     * Normalizes row-action text before comparing the deployment button order.
                                     * @param {string} text - Rendered label or context text to inspect.
                                     * @returns {string} Trimmed action label.
                                     */
                                    text => text.trim()), ['Deploy', 'Stop', 'Remove']);
                            }
                            assert.equal(await page.locator('article.dashboard-project-card').count(), 0);
                            if (width === 360) assert.equal(await page.locator('.project-inventory thead').evaluate(
                                /**
                                 * Reads the computed mobile table-header display mode.
                                 * @param {Element} e - Rendered element to filter, format or inspect.
                                 * @returns {string} Computed CSS display value.
                                 */
                                e => getComputedStyle(e).display), 'none');
                        }
                        if (name === 'details') {
                            const dimensions = await page.locator('.terminal-panel').evaluate(
                                /**
                                 * Measures terminal width, parent width and viewport height.
                                 * @param {Element} panel - Rendered fixture element to inspect.
                                 * @returns {object} Terminal dimensions in CSS pixels.
                                 */
                                panel => ({
                                    width: panel.getBoundingClientRect().width,
                                    parent: panel.parentElement.clientWidth,
                                    height: panel.querySelector('.terminal-panel-body').getBoundingClientRect().height
                                }));
                            assert.ok(dimensions.height >= 440, 'Project terminals have a taller viewport');
                            if (width === 1440) assert.ok(dimensions.width < dimensions.parent * .96, 'Desktop terminal width is reduced');
                        }
                        if (name.startsWith('overview')) {
                            assert.equal(await page.locator('.console-kpi').count(), 4);
                            assert.equal(await page.locator('.telemetry-chart').count(), 2);
                            assert.ok(!(await page.locator('body').innerText()).includes('All systems operational'));
                        }
                        if (name === 'configuration' || name.startsWith('azure')) {
                            const dialog = page.locator('[role=dialog]');
                            assert.equal(await dialog.getAttribute('aria-modal'), 'true');
                            assert.ok(await page.evaluate(
                                /**
                                 * Checks that initial dialog focus is inside the modal.
                                 * @returns {boolean} True when active focus has a dialog ancestor.
                                 */
                                () => document.activeElement.closest('[role=dialog]') !== null));
                            const buttons = dialog.locator('button,a,input,select');
                            await buttons.last().focus();
                            await page.keyboard.press('Tab');
                            assert.ok(await page.evaluate(
                                /**
                                 * Checks that Tab traversal remains within the dialog.
                                 * @returns {boolean} True when active focus has a dialog ancestor.
                                 */
                                () => document.activeElement.closest('[role=dialog]') !== null), 'Focus stays in dialog');
                            await page.keyboard.press('Escape');
                            assert.equal(await page.locator('[role=dialog]').count(), 0);
                        }
                        if (name === 'details') {
                            assert.equal(await page.locator('.deployment-progress').count(), 0);
                            assert.equal(await page.locator('.overview-layout > .detail-panel').count(), 4);
                            assert.deepEqual(await page.locator('.project-section-tabs a').allTextContents(), ['Overview', 'Live Logs', 'Live Resource Utilization', 'Project Analytics', 'Configuration', 'Deployment History']);
                            assert.ok(await page.locator('.configuration-tables table').count() >= 4);
                            await page.locator('#configuration .console-details > summary').click();
                            assert.ok(await page.locator('#configuration .console-details').getAttribute('open') !== null);
                        }
                    }
                }
            }

            for (const theme of ['light', 'dark']) {
                await page.setViewportSize({width: 768, height: 960});
                await page.goto(url + '/history.html');
                await page.evaluate(
                    /**
                     * Applies the selected theme before saved-analysis presentation checks.
                     * @param {string} theme - Light or dark fixture theme.
                     * @returns {string} The assigned theme identifier.
                     */
                    theme => document.documentElement.dataset.bsTheme = theme, theme);
                assert.equal(await page.locator('.deployment-identity-status .deployment-badge').count(), 1);
                const accent = page.locator('.ai-analysis-answer').first();
                assert.equal(await accent.evaluate(
                    /**
                     * Reads the generated-answer border width.
                     * @param {Element} e - Rendered element to filter, format or inspect.
                     * @returns {string} Computed top-border width.
                     */
                    e => getComputedStyle(e).borderTopWidth), '2px');
                assert.equal(await accent.evaluate(
                    /**
                     * Reads the generated-answer border color.
                     * @param {Element} e - Rendered element to filter, format or inspect.
                     * @returns {string} Computed top-border color.
                     */
                    e => getComputedStyle(e).borderTopColor), theme === 'dark' ? 'rgb(192, 132, 252)' : 'rgb(147, 51, 234)');
                assert.equal(await accent.locator('button,input,select,dl').count(), 0, 'Only the generated answer receives the purple border');
                await accent.screenshot({path: path.join(preview, 'ai-history-' + theme + '.png')});
                await page.locator('.telemetry-explore > summary').click();
                await page.locator('.telemetry-table-details > summary').click();
                assert.ok(await page.locator('.statistics-pager').count() > 0);
                for (const value of ['10m', '30m', '1h', '6h', '24h', '7d', '30d', '90d', '1y', '5y', 'custom'])
                    assert.ok(await page.locator('.metric-range-picker option[value="' + value + '"]').count() > 0);
                await page.locator('[aria-labelledby=history-metrics-title]').screenshot({path: path.join(preview, 'metric-controls-' + theme + '.png')});
                await page.goto(url + '/configuration.html');
                await page.evaluate(
                    /**
                     * Applies the selected theme before configuration-action checks.
                     * @param {string} theme - Light or dark fixture theme.
                     * @returns {string} The assigned theme identifier.
                     */
                    theme => document.documentElement.dataset.bsTheme = theme, theme);
                assert.equal(await page.locator('.modal-footer').evaluate(
                    /**
                     * Reads configuration-action spacing.
                     * @param {Element} e - Rendered element to filter, format or inspect.
                     * @returns {string} Computed CSS gap value.
                     */
                    e => getComputedStyle(e).gap), '16px');
                await page.locator('.modal-footer').screenshot({path: path.join(preview, 'deployment-actions-' + theme + '.png')});
                await page.goto(url + '/projects.html');
                assert.equal(await page.locator('.azure-connection-strip > .console-row-actions').first().evaluate(
                    /**
                     * Reads Azure-connection action spacing.
                     * @param {Element} e - Rendered element to filter, format or inspect.
                     * @returns {string} Computed CSS gap value.
                     */
                    e => getComputedStyle(e).gap), '16px');
                await page.goto(url + '/local.html');
                assert.equal(await page.locator('details.local-subproject-list').count(), 0);
                await page.goto(url + '/details.html');
                await page.evaluate(
                    /**
                     * Applies the selected theme before capturing the project analysis dialog.
                     * @param {string} theme - Light or dark fixture theme.
                     * @returns {string} The assigned theme identifier.
                     */
                    theme => document.documentElement.dataset.bsTheme = theme, theme);
                await page.locator('.terminal-ai-shortcut').click();
                await page.locator('[role=dialog]').screenshot({path: path.join(preview, 'ai-project-' + theme + '.png')});
            }
            for (const name of ['overview', 'projects', 'login', 'details', 'history', 'github', 'local', 'configuration']) {
                await page.setViewportSize({width: 1440, height: 960});
                await page.goto(url + '/' + name + '.html');
                await page.evaluate(
                    /**
                     * Applies 200 percent text/viewport zoom to the preview.
                     * @returns {string} The assigned CSS zoom value.
                     */
                    () => document.documentElement.style.zoom = '2');
                await page.evaluate(
                    /**
                     * Waits for preview fonts before measuring zoom overflow.
                     * @returns {Promise<FontFaceSet>} Resolves when font loading completes.
                     */
                    () => document.fonts.ready);
                const zoomOverflow = await page.evaluate(
                    /**
                     * Captures overflow after 200 percent zoom.
                     * @returns {object} Document and viewport widths with at most ten overflowing element labels.
                     */
                    () => ({
                        width: document.documentElement.scrollWidth, viewport: innerWidth,
                        elements: [...document.querySelectorAll('body *')].filter(
                            /**
                             * Finds elements extending beyond the zoomed viewport.
                             * @param {Element} e - Rendered element to filter, format or inspect.
                             * @returns {boolean} True when the right edge exceeds the viewport by more than one pixel.
                             */
                            e => e.getBoundingClientRect().right > innerWidth + 1)
                            .slice(0, 10).map(
                                /**
                                 * Labels an overflowing element for the zoom assertion.
                                 * @param {Element} e - Rendered element to filter, format or inspect.
                                 * @returns {string} Element tag and class names.
                                 */
                                e => e.tagName + '.' + e.className)
                    }));
                assert.ok(zoomOverflow.width <= zoomOverflow.viewport + 1, name + ' at 200% zoom must not overflow: ' + JSON.stringify(zoomOverflow));
            }
            await page.goto(url + '/overview.html');
            await page.setViewportSize({width: 360, height: 960});
            const toggle = page.locator('.mobile-nav-toggle');
            await toggle.click();
            assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
            await page.goto(url + '/details.html#configuration');
            await page.waitForFunction(
                /**
                 * Waits for fragment navigation to focus the configuration heading.
                 * @returns {boolean} True when the configuration heading owns focus.
                 */
                () => document.activeElement.id === 'configuration-title');
            await page.locator('.terminal-ai-shortcut').focus();
            await page.keyboard.press('Enter');
            await page.waitForFunction(
                /**
                 * Waits for keyboard-opened analysis dialog focus.
                 * @returns {boolean|undefined} Whether the current dialog contains focus, or undefined before it exists.
                 */
                () => document.querySelector('[role=dialog]')?.contains(document.activeElement));
            await page.keyboard.press('Escape');
            await page.waitForFunction(
                /**
                 * Waits for dialog dismissal and focus restoration to its terminal shortcut.
                 * @returns {boolean} True once the dialog is removed and the shortcut owns focus.
                 */
                () => !document.querySelector('[role=dialog]') && document.activeElement.classList.contains('terminal-ai-shortcut'));
            assert.deepEqual(errors, []);
            console.log('Passed ' + checked + ' theme/viewport page checks, dialog keyboard traps, selected deployment links, section anchors, mobile navigation and 200% zoom. Screenshots: ' + preview);
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
