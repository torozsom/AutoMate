// Runs against provider-free HTML rendered by TelemetryPageRenderingTests.
const fs = require('fs');
const path = require('path');
const http = require('http');
const assert = require('node:assert/strict');
const {chromium} = require(process.env.AUTOMATE_PLAYWRIGHT_MODULE || 'playwright');
const root = path.resolve(__dirname, '../..');
const preview = path.join(root, '.artifacts/metrics-preview');
const assets = path.join(root, 'Web/wwwroot');
const server = http.createServer((req, res) => {
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
(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const browser = await chromium.launch({headless: true, channel: process.env.AUTOMATE_BROWSER_CHANNEL || 'msedge'});
    try {
        const address = 'http://127.0.0.1:' + server.address().port;
        const page = await browser.newPage({viewport: {width: 1280, height: 900}, reducedMotion: 'reduce'});
        page.setDefaultTimeout(5000);
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        page.on('pageerror', e => console.error('Browser script error:', e.message));
        page.on('console', message => {
            if (message.type() === 'error') console.error(message.text());
        });
        page.on('requestfailed', request => console.error('Failed resource:', request.url(), request.failure()?.errorText));
        await page.goto(address + '/details.html#configuration');
        await page.waitForFunction(() => document.activeElement?.id === 'configuration-title');
        assert.equal(await page.locator('.project-section-tabs a').count(), 6);
        assert.equal(await page.locator('#ai-analysis').count(), 0);
        await page.locator('button.terminal-ai-shortcut').click();
        await page.waitForFunction(() => document.querySelector('[role=dialog]')?.contains(document.activeElement));
        assert.equal(await page.locator('.assessment-context select').count(), 2);
        assert.equal(await page.locator('.assessment-context select').first().locator('option').count(), 5);
        assert.equal(await page.locator('.assessment-context select').last().locator('option').count(), 6);
        assert.ok(await page.locator('.assessment-context').innerText().then(text => text.includes('Azure application console output')));
        await page.keyboard.press('Escape');
        await page.waitForFunction(() => !document.querySelector('[role=dialog]') && document.activeElement?.classList.contains('terminal-ai-shortcut'));
        await page.locator('.project-section-tabs a[href="#logs"]').click();
        await page.waitForFunction(() => document.activeElement?.id === 'logs-title');
        await page.goBack();
        await page.waitForFunction(() => location.hash === '#configuration');
        // Keyboard activation uses the same real anchors and moves focus to the section heading.
        await page.locator('.project-section-tabs a[href="#overview"]').focus();
        await page.keyboard.press('Enter');
        await page.waitForFunction(() => document.activeElement?.id === 'overview-title');
        await page.screenshot({path: path.join(preview, 'details-light.png'), fullPage: true});
        await page.evaluate(() => document.documentElement.dataset.bsTheme = 'dark');
        await page.screenshot({path: path.join(preview, 'details-dark.png'), fullPage: true});
        await page.setViewportSize({width: 390, height: 844});
        await page.goto(address + '/details.html#overview');
        assert.ok(await page.locator('.project-section-tabs').evaluate(nav => nav.scrollWidth > nav.clientWidth));
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'No page-wide mobile overflow');
        assert.ok(await page.locator('.terminal-ai-shortcut').evaluate(link => {
            const box = link.getBoundingClientRect();
            return box.left >= 0 && box.right <= innerWidth;
        }), 'AI shortcut remains visible on mobile');
        await page.screenshot({path: path.join(preview, 'details-mobile.png'), fullPage: true});
        await page.goto(address + '/history.html');
        assert.equal(await page.locator('.assessment-context').count(), 1);
        assert.ok(await page.locator('#history-logs-title').evaluate(logs =>
            logs.getBoundingClientRect().top < document.querySelector('#history-metrics-title').getBoundingClientRect().top));
        await page.screenshot({path: path.join(preview, 'history-mobile.png'), fullPage: true});
        await page.setViewportSize({width: 1280, height: 900});
        await page.screenshot({path: path.join(preview, 'history-light.png'), fullPage: true});
        await page.evaluate(() => document.documentElement.dataset.bsTheme = 'dark');
        await page.screenshot({path: path.join(preview, 'history-dark.png'), fullPage: true});
        assert.deepEqual(errors, []);
        console.log('Browser checks passed: direct fragments, anchors, focus, back navigation, reduced motion, mobile overflow, section order and both themes.');
    } finally {
        await browser.close();
        server.close();
    }
})().catch(e => {
    console.error(e);
    server.close();
    process.exitCode = 1;
});
