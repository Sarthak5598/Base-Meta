// Optional end-to-end check: drives the real UI in a browser through connect, build, chart, save, dashboard.
// Needs: the sample database running (docker compose up -d), a Playwright install, and a FRESH data folder.
//   cd web && npm install --no-save playwright && npx playwright install chromium
//   cd .. && DATALENS_HOME=$(mktemp -d) dotnet run --project src/DataLens.Api &
//   node scripts/e2e.cjs <folder for screenshots>
// Exits 0 only if every check passed and the browser logged no errors.
const { chromium } = require('playwright');
const SP = process.argv[2] || '.';
const URL = 'http://localhost:5080';
const results = [];
const check = (name, ok, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  ' + detail : ''}`); };

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ viewport: { width: 1360, height: 900 } });
  const page = await ctx.newPage();
  const problems = [];
  page.on('console', m => { if (m.type() === 'error') problems.push('console: ' + m.text()); });
  page.on('pageerror', e => problems.push('pageerror: ' + e.message));
  const shot = (n) => page.screenshot({ path: `${SP}/${n}.png` });
  const runAndWait = async () => {
    await page.click('button:has-text("Run")');
    await page.waitForFunction(() => document.querySelector('.result') && !document.querySelector('button.primary')?.textContent.includes('Running'));
  };

  // 1. first run: no connections, so the connect form shows
  await page.goto(URL);
  await page.waitForSelector('form.connect');
  await page.fill('#conn-name', 'shop');
  await page.fill('#conn-string', 'postgresql://datalens_ro:readonly@127.0.0.1:5433/shop');
  await page.click('button:has-text("Connect and read structure")');
  await page.waitForSelector('.browse table.grid tbody tr');
  check('connects and lands on Browse data', true);

  // 2. new question: orders per month
  await page.click('.nav-item:has-text("New question")');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.orders');
  await page.click('button:has-text("+ Add a summary")');
  await page.click('button:has-text("+ Group by")');
  await page.waitForFunction(() => !document.querySelector('.builder-actions button.primary').disabled);
  await page.click('button:has-text("Show SQL")');
  const sql1 = await page.locator('pre.sql').innerText();
  check('live SQL shows date_trunc month + COUNT + GROUP BY', /date_trunc\('month'/.test(sql1) && /COUNT\(\*\)/.test(sql1) && /GROUP BY 1/.test(sql1), JSON.stringify(sql1.split('\n').slice(0, 2)));
  await runAndWait();
  check('suggests a line chart for dates + a count', await page.locator('.segmented button.on').innerText() === 'Line');
  const dots = await page.locator('.recharts-line-dots circle, .recharts-line-dot').count();
  const linePath = await page.locator('path.recharts-line-curve').count();
  check('line is drawn with dots', linePath === 1 && dots >= 6, `path=${linePath} dots=${dots}`);
  const ticks = (await page.locator('.recharts-cartesian-axis-tick-value').evaluateAll(els => els.map(e => e.textContent))).filter(t => /^[A-Z][a-z]{2} \d{4}$/.test(t));
  const dates = ticks.map(t => new Date('1 ' + t.replace(/\s+/g, ' ')).getTime());
  check('the line runs oldest to newest with no sort chosen', dates.length >= 4 && dates.every((d, i) => i === 0 || d > dates[i - 1]), JSON.stringify(ticks));
  const status1 = await page.locator('.builder-actions .small').innerText();
  check('shows row count and timing', /\d+ rows? · \d+ ms/.test(status1), status1);
  await shot('1-line');

  // sort ascending by created_at; first x label should be the oldest month
  check('default order is shown in the SQL', /ORDER BY 1 ASC NULLS LAST/.test(await page.locator('pre.sql').innerText()));
  await page.selectOption('select[aria-label="Sort by"]', 'created_at');
  await page.selectOption('select[aria-label="Sort direction"]', 'desc');
  await page.waitForFunction(() => document.querySelector('pre.sql').textContent.includes('"created_at" DESC'));
  await runAndWait();
  check('choosing a sort replaces the default', /ORDER BY "created_at" DESC/.test(await page.locator('pre.sql').innerText()) && !/ORDER BY 1/.test(await page.locator('pre.sql').innerText()));
  await page.selectOption('select[aria-label="Sort by"]', '');
  await page.waitForFunction(() => document.querySelector('pre.sql').textContent.includes('ORDER BY 1'));
  await runAndWait();

  // 3. a filter
  const totalBefore = await page.evaluate(() => [...document.querySelectorAll('.grid')].length);
  await page.click('button:has-text("+ Add a filter")');
  const filterCol = page.locator('select[aria-label="Filter column"]').first();
  await filterCol.selectOption({ label: 'status' });
  await page.locator('select[aria-label="Filter condition"]').first().selectOption('eq');
  await page.locator('input[aria-label="Value"]').first().fill('paid');
  await page.waitForFunction(() => document.querySelector('pre.sql').textContent.includes('WHERE'));
  check('filter appears as a parameter, not text', /"t0"\."status" = @p1/.test(await page.locator('pre.sql').innerText()) && !/paid/.test((await page.locator('pre.sql').innerText()).split('\n').slice(0,8).join(' ')));
  check('parameter value is listed separately', (await page.locator('.params').innerText()).includes('paid'));
  await runAndWait();
  await shot('2-filtered');

  // 4. switch the chart type: bar, then pie after grouping by status
  await page.click('.segmented button:has-text("Bar")');
  await page.waitForSelector('.recharts-bar-rectangle');
  check('a month axis stays as columns, not rows', await page.locator('.recharts-bar-rectangle path').first().evaluate(el => { const b = el.getBBox(); return b.height > b.width || b.width < 30; }));
  const bars = await page.locator('.recharts-bar-rectangle').count();
  check('bar chart draws bars', bars >= 6, `bars=${bars}`);
  const barWidth = await page.locator('.recharts-bar-rectangle path').first().evaluate(el => { const b = el.getBBox(); return Math.min(b.width, b.height); });
  check('bars are at most 24px thick', barWidth <= 24.5, `width=${barWidth.toFixed(1)}`);
  await shot('3-bar');

  // 5. new question: status shares as a pie, no filter
  await page.click('.nav-item:has-text("New question")');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.orders');
  await page.click('button:has-text("+ Add a summary")');
  await page.click('button:has-text("+ Group by")');
  await page.selectOption('select[aria-label="Group by column"]', { label: 'status' });
  await page.waitForFunction(() => !document.querySelector('.builder-actions button.primary').disabled);
  await runAndWait();
  check('text + count suggests a bar chart', await page.locator('.segmented button.on').innerText() === 'Bar');
  await page.click('.segmented button:has-text("Pie")');
  await page.waitForSelector('.recharts-pie-sector');
  const slices = await page.locator('.recharts-pie-sector').count();
  const legend = (await page.locator('.pie-legend').innerText()).replace(/\s+/g, ' ');
  check('pie has 4 slices and a legend with names, amounts and shares', slices === 4 && /paid/.test(legend) && /%/.test(legend), legend);
  await shot('4-pie');
  await page.click('.segmented button:has-text("Table")');
  const tableRows = await page.locator('.result table.grid tbody tr').count();
  check('table view of the same result has 4 rows', tableRows === 4);

  // 6. join through a foreign key: orders per country
  await page.click('.nav-item:has-text("New question")');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.orders');
  const related = await page.locator('select[aria-label="Add a related table"] option').allInnerTexts();
  check('offers tables linked by foreign keys', related.some(t => /users, through orders\.user_id/.test(t)) && related.some(t => /order_items, through order_items\.order_id/.test(t)), JSON.stringify(related));
  await page.selectOption('select[aria-label="Add a related table"]', { label: related.find(t => /^users/.test(t)) });
  await page.click('button:has-text("+ Add a summary")');
  await page.click('button:has-text("+ Group by")');
  await page.selectOption('select[aria-label="Group by column"]', { label: 'users.country' });
  await page.waitForFunction(() => !document.querySelector('.builder-actions button.primary').disabled);
  const sql2 = await page.locator('pre.sql').innerText().catch(async () => { await page.click('button:has-text("Show SQL")'); return page.locator('pre.sql').innerText(); });
  check('the ON clause is written for the user', /LEFT JOIN "public"\."users" AS "t1" ON "t0"\."user_id" = "t1"\."id"/.test(sql2));
  await runAndWait();
  const countryRows = await page.evaluate(() => 0);
  await page.click('.segmented button:has-text("Table")');
  const cells = await page.locator('.result table.grid tbody tr td:last-child').allInnerTexts();
  const total = cells.reduce((s, c) => s + Number(c.replace(/,/g, '')), 0);
  check('orders per country add up to all 2000 orders', total === 2000, `rows=${cells.length} total=${total}`);
  await page.click('.segmented button:has-text("Bar")');
  await page.waitForSelector('.recharts-bar-rectangle');
  await shot('5-join-bar');

  // 7. save it
  await page.fill('input[placeholder="Orders per month"]', 'Orders by country');
  await page.click('button:has-text("Save question")');
  await page.waitForSelector('.save-note');
  check('saves the question', /Saved/.test(await page.locator('.save-note').innerText()));

  // 8. a saved list, a dashboard, and a card that runs live
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('#dash-name');
  await page.fill('#dash-name', 'Sales overview');
  await page.click('button:has-text("Create")');
  await page.waitForSelector('h2:has-text("Sales overview")');
  await page.selectOption('select[aria-label="Add a question"]', { label: 'Orders by country' });
  await page.waitForSelector('.dash-card .recharts-bar-rectangle');
  check('dashboard card runs the saved question and draws its chart', true);
  await shot('6-dashboard');

  // 9. save a second one (line) and see both on the dashboard
  await page.click('.nav-item:has-text("New question")');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.orders');
  await page.click('button:has-text("+ Add a summary")');
  await page.click('button:has-text("+ Group by")');
  await page.selectOption('select[aria-label="Sort by"]', { label: 'created_at' }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.builder-actions button.primary').disabled);
  await page.selectOption('select[aria-label="Sort by"]', 'created_at');
  await runAndWait();
  await page.fill('input[placeholder="Orders per month"]', 'Orders per month');
  await page.click('button:has-text("Save question")');
  await page.waitForSelector('.save-note');
  await page.click('.nav-item:has-text("Saved questions")');
  await page.waitForSelector('.list-card');
  const names = await page.locator('.list-card .strong').allInnerTexts();
  check('saved list shows both questions', names.includes('Orders by country') && names.includes('Orders per month'), JSON.stringify(names));
  await page.locator('.list-card:has-text("Orders per month") select').selectOption({ label: 'Sales overview' });
  await page.waitForSelector('.banner.ok');
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.dash-card .recharts-line-curve');
  check('dashboard now has a bar card and a line card', (await page.locator('.dash-card').count()) === 2);
  await shot('7-dashboard-two');

  // 10. open a saved question: prefilled and already run
  await page.click('.nav-item:has-text("Saved questions")');
  await page.locator('.list-card:has-text("Orders by country") button:has-text("Open")').click();
  await page.waitForSelector('.result');
  check('opening a saved question restores the form and runs it',
    (await page.locator('select[aria-label="Table to start from"]').inputValue()) === 'public.orders'
    && (await page.locator('select[aria-label="Group by column"]').inputValue()).includes('country')
    && (await page.locator('h1').first().innerText()) === 'Edit question');
  await shot('8-edit-saved');

  // 11. delete with confirmation
  await page.click('.nav-item:has-text("Saved questions")');
  await page.waitForSelector('.list-card');
  const before = await page.locator('.list-card').count();
  await page.locator('.list-card:has-text("Orders by country") button:has-text("Delete")').click();
  await page.locator('button:has-text("Yes, delete")').click();
  await page.waitForFunction((n) => document.querySelectorAll('.list-card').length === n - 1, before);
  check('deleting asks first, then removes the question', true);
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.dash-card');
  check('and removes it from the dashboard', (await page.locator('.dash-card').count()) === 1);

  console.log('\nconsole/page errors:', problems.length ? problems : 'none');
  console.log(`${results.filter(Boolean).length}/${results.length} checks passed`);
  await browser.close();
  process.exit(results.every(Boolean) && problems.length === 0 ? 0 : 1);
})().catch(e => { console.error('SCRIPT FAILED:', e.message.split('\n')[0]); process.exit(2); });
