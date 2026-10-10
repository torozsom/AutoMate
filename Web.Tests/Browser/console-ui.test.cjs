// Real component HTML from ConsoleRenderingTests and TelemetryPageRenderingTests; no OAuth or provider calls.
const fs = require('fs'), path = require('path'), http = require('http'), assert = require('node:assert/strict');
const {chromium} = require(process.env.AUTOMATE_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..'), preview = path.join(root, '.artifacts/console-preview'),
    metrics = path.join(root, '.artifacts/metrics-preview'), assets = path.join(root, 'Web/wwwroot');
const server = http.createServer((req, res) => {
    const name = new URL(req.url, 'http://localhost').pathname.replace(/^\//, '');
    if (name === 'favicon.ico') {
        res.writeHead(204);
        res.end();
        return;
    }
    const base = /^(details|history)\.html$/.test(name) ? metrics : /^[a-z-]+\.html$|^(bootstrap.css|Web.styles.css)$/.test(name) ? preview : assets;
    const file = path.resolve(base, name);
    if (!file.startsWith(base + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
        res.writeHead(404);
        res.end();
        return;
    }
    res.setHeader('Content-Type', file.endsWith('.js') ? 'text/javascript' : file.endsWith('.css') ? 'text/css' : 'text/html');
    fs.createReadStream(file).pipe(res);
});
(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({headless: true, channel: process.env.AUTOMATE_BROWSER_CHANNEL || 'msedge'});
    const errors = [];
    let checked = 0;
    try {
        const page = await browser.newPage({reducedMotion: 'reduce'});
        page.on('pageerror', e => errors.push(e.message));
        const url = 'http://127.0.0.1:' + server.address().port;
        for (const name of ['overview', 'projects', 'azure', 'github', 'local', 'landing', 'login', 'register', 'verify', 'configuration', 'details', 'history', 'overview-saas', 'projects-saas', 'azure-saas']) {
            for (const theme of ['light', 'dark']) {
                for (const width of [360, 768, 1440]) {
                    await page.setViewportSize({width, height: 960});
                    await page.goto(url + '/' + name + '.html');
                    await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
                    await page.evaluate(() => document.fonts.ready);
                    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), name + ' ' + theme + ' ' + width + ' must not overflow');
                    await page.screenshot({
                        path: path.join(preview, name + '-' + theme + '-' + width + '.png'),
                        fullPage: true
                    });
                    checked++;
                    const lowContrast = await page.evaluate(() => {
                        const rgb = value => (value.match(/[0-9.]+/g) || []).slice(0, 3).map(v => Number(v) * (value.startsWith('color(srgb') ? 255 : 1));
                        const luminance = value => rgb(value).map(v => {
                            v /= 255;
                            return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
                        }).reduce((sum, v, i) => sum + v * [.2126, .7152, .0722][i], 0);
                        const background = element => {
                            for (let e = element; e; e = e.parentElement) {
                                const c = getComputedStyle(e).backgroundColor;
                                if (c !== 'rgba(0, 0, 0, 0)' && c !== 'transparent') return c;
                            }
                            return getComputedStyle(document.body).backgroundColor;
                        };
                        return [...document.querySelectorAll('.console-kpi span,.console-meta,.console-subtext,.page-description,.btn-primary')]
                            .filter(e => e.getClientRects().length && !e.matches(':disabled')).filter(e => {
                                const style = getComputedStyle(e);
                                const a = luminance(style.color), b = luminance(background(e));
                                return (Math.max(a, b) + .05) / (Math.min(a, b) + .05) < 4.5;
                            }).map(e => e.textContent.slice(0, 80));
                    });
                    assert.deepEqual(lowContrast, [], name + ' ' + theme + ' text contrast');
                    if (name.startsWith('projects')) {
                        assert.equal(await page.locator('.project-inventory tbody tr').count(), 3);
                        assert.equal(await page.locator('article.dashboard-project-card').count(), 0);
                        if (width === 360) assert.equal(await page.locator('.project-inventory thead').evaluate(e => getComputedStyle(e).display), 'none');
                    }
                    if (name.startsWith('overview')) {
                        assert.equal(await page.locator('.console-kpi').count(), 4);
                        assert.equal(await page.locator('.telemetry-chart').count(), 2);
                        assert.ok(!(await page.locator('body').innerText()).includes('All systems operational'));
                    }
                    if (name === 'configuration' || name.startsWith('azure')) {
                        const dialog = page.locator('[role=dialog]');
                        assert.equal(await dialog.getAttribute('aria-modal'), 'true');
                        assert.ok(await page.evaluate(() => document.activeElement.closest('[role=dialog]') !== null));
                        const buttons = dialog.locator('button,a,input,select');
                        await buttons.last().focus();
                        await page.keyboard.press('Tab');
                        assert.ok(await page.evaluate(() => document.activeElement.closest('[role=dialog]') !== null), 'Focus stays in dialog');
                        await page.keyboard.press('Escape');
                        assert.equal(await page.locator('[role=dialog]').count(), 0);
                    }
                    if (name === 'details') {
                        assert.deepEqual(await page.locator('.project-section-tabs a').allTextContents(), ['Overview', 'Live Logs', 'Live Resource Utilization', 'Project Analytics', 'AI Analysis', 'Configuration', 'Deployment History']);
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
            await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
            assert.equal(await page.locator('.deployment-identity-status .deployment-badge').count(), 1);
            const accent = page.locator('.ai-analysis-accent');
            assert.equal(await accent.evaluate(e => getComputedStyle(e).borderTopWidth), '2px');
            await accent.screenshot({path: path.join(preview, 'ai-history-' + theme + '.png')});
            await page.locator('.telemetry-explore > summary').click();
            await page.locator('.telemetry-table-details > summary').click();
            assert.ok(await page.locator('.statistics-pager').count() > 0);
            for (const value of ['10m', '30m', '1h', '6h', '24h', '7d', '30d', '90d', '1y', '5y', 'custom'])
                assert.ok(await page.locator('.metric-range-picker option[value="' + value + '"]').count() > 0);
            await page.locator('[aria-labelledby=history-metrics-title]').screenshot({path: path.join(preview, 'metric-controls-' + theme + '.png')});
            await page.goto(url + '/configuration.html');
            await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
            assert.equal(await page.locator('.modal-footer').evaluate(e => getComputedStyle(e).gap), '16px');
            await page.locator('.modal-footer').screenshot({path: path.join(preview, 'deployment-actions-' + theme + '.png')});
            await page.goto(url + '/projects.html');
            assert.equal(await page.locator('.azure-connection-strip > .console-row-actions').first().evaluate(e => getComputedStyle(e).gap), '16px');
            await page.goto(url + '/local.html');
            assert.equal(await page.locator('details.local-subproject-list').count(), 0);
            await page.goto(url + '/details.html');
            await page.evaluate(theme => document.documentElement.dataset.bsTheme = theme, theme);
            await page.locator('#ai-analysis').screenshot({path: path.join(preview, 'ai-project-' + theme + '.png')});
        }
        for (const name of ['overview', 'projects', 'login', 'details', 'history', 'github', 'local', 'configuration']) {
            await page.setViewportSize({width: 1440, height: 960});
            await page.goto(url + '/' + name + '.html');
            await page.evaluate(() => document.documentElement.style.zoom = '2');
            await page.evaluate(() => document.fonts.ready);
            assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), name + ' at 200% zoom must not overflow');
        }
        await page.goto(url + '/overview.html');
        await page.setViewportSize({width: 360, height: 960});
        const toggle = page.locator('.mobile-nav-toggle');
        await toggle.click();
        assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
        await page.goto(url + '/details.html#configuration');
        await page.waitForFunction(() => document.activeElement.id === 'configuration-title');
        await page.locator('.terminal-ai-shortcut').focus();
        await page.keyboard.press('Enter');
        await page.waitForFunction(() => document.activeElement.id === 'ai-analysis-title');
        assert.deepEqual(errors, []);
        console.log('Passed ' + checked + ' theme/viewport page checks, dialog keyboard traps, selected deployment links, section anchors, mobile navigation and 200% zoom. Screenshots: ' + preview);
    } finally {
        await browser.close();
        server.close();
    }
})().catch(e => {
    console.error(e);
    server.close();
    process.exitCode = 1;
});
