const { chromium } = require('playwright');

(async () => {
    const browser = await chromium.launch({ headless: true, channel: process.env.BROWSER_SMOKE_CHANNEL });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        if (process.env.BROWSER_SMOKE_DEBUG) page.on('console', message => console.log(message.type(), message.text()));
        const base = process.env.BROWSER_SMOKE_URL || 'http://127.0.0.1:5189';
        await page.request.post(base + '/reset');
        await page.goto(base);
        await page.getByRole('button', { name: 'Export telemetry' }).click();
        await page.waitForFunction(() => /HTTP traceparent: 00-[a-f0-9]{32}-[a-f0-9]{16}-01/.test(document.querySelector('#result').textContent));
        let signals = {};
        for (let attempt = 0; attempt < 45; attempt++) {
            signals = await (await page.request.get(base + '/received')).json();
            if (['logs', 'metrics', 'traces'].every(signal => signals[signal] > 0)) break;
            await new Promise(resolve => setTimeout(resolve, 1000));
        }
        if (!['logs', 'metrics', 'traces'].every(signal => signals[signal] > 0)) throw new Error('Missing signals: ' + JSON.stringify(signals));
        if (errors.length) throw new Error(errors.join('\n'));
        await page.request.post(base + '/unavailable');
        const previous = await page.locator('#result').innerText();
        await page.getByRole('button', { name: 'Export telemetry' }).click();
        await page.waitForFunction(value => document.querySelector('#result').textContent !== value, previous);
        await page.waitForTimeout(6000);
        if (errors.length) throw new Error(errors.join('\n'));
        console.log(JSON.stringify({ browser: 'Chromium', signals, collectorUnavailable: 'UI remains responsive', errors }));
    } finally {
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
