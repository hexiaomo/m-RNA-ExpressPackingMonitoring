import assert from 'node:assert/strict';
import test from 'node:test';
import { chromium } from 'playwright-core';

const baseUrl = process.env.EPM_AUTOMATION_BASE_URL;

test('isolated Web server supports search, playback and clip editor entry', { skip: !baseUrl }, async () => {
  const executablePath = process.env.EPM_BROWSER_EXECUTABLE;
  assert.ok(executablePath, 'EPM_BROWSER_EXECUTABLE is required');
  const browser = await chromium.launch({ executablePath, headless: true });
  try {
    const page = await browser.newPage();
    await page.goto(baseUrl, { waitUntil: 'networkidle' });
    await assert.doesNotReject(() => page.getByRole('heading', { name: '快递打包录像回放' }).waitFor());

    const search = page.getByPlaceholder('输入订单号关键词搜索');
    await search.fill('AUTO_WEB_001');
    await search.press('Enter');
    const article = page.locator('article').filter({ hasText: 'AUTO_WEB_001' });
    await assert.doesNotReject(() => article.waitFor());

    await article.getByRole('button', { name: '播放' }).click();
    await assert.doesNotReject(() => page.locator('#playerOverlay.active').waitFor());
    const source = await page.locator('#videoPlayer').getAttribute('src');
    assert.match(source ?? '', /\/api\/videos\/\d+\/play/);
    await page.keyboard.press('Escape');

    await article.getByRole('button', { name: '剪辑' }).click();
    await assert.doesNotReject(() => page.locator('#clipOverlay.active').waitFor());
  } finally {
    await browser.close();
  }
});
