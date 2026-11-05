// Optional end-to-end check: drives the real UI in a browser through connect, build, chart, save, and a
// dashboard with every kind of card and its date range and granularity controls.
// Needs: the sample database running (docker compose up -d), a Playwright install, and a FRESH data folder.
//   cd web && npm install --no-save playwright && npx playwright install chromium
//   cd .. && DATALENS_HOME=$(mktemp -d) dotnet run --project src/DataLens.Api &
//   node scripts/e2e.cjs <folder for screenshots>
// Exits 0 only if every check passed and the browser logged no errors.
// (Some checks assume the sample data's dates are recent, as they are right after the database is created.)
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


  // ======================= dashboards: the add-card flow ======================================
  const cardRows = async (name) => {
    const t = await page.locator(`.dash-card:has-text("${name}") footer`).innerText();
    return Number(t.match(/([\d,]+) rows/)[1].replace(/,/g, ''));
  };
  const settled = () => page.waitForFunction(() => !document.querySelector('.dash-card.loading'));
  const addCardFromTopButton = async (kind) => {
    await page.click('button:has-text("+ Add card")');
    await page.waitForSelector('.dialog');
    await page.locator(`.dialog .type-tile:has-text("${kind}")`).click();
  };
  const pickTableAndAdd = async (table, expectChart, nameExpect) => {
    await page.selectOption('select[aria-label="Table to start from"]', `public.${table}`);
    await page.waitForSelector('.result');
    if (expectChart) await page.waitForSelector(expectChart);
    if (nameExpect) check(`suggests the name "${nameExpect}"`, (await page.locator('.save input').inputValue()) === nameExpect, await page.locator('.save input').inputValue());
    await page.click('button:has-text("Add to dashboard")');
    await page.waitForSelector('.timebar');
  };

  // 8. no dashboards yet: the first thing to do is make one
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.empty-dash h2:has-text("Create your first dashboard")');
  await shot('6-first-dashboard');
  await page.fill('#dash-name', 'Sales overview');
  await page.click('button:has-text("Create")');
  await page.waitForSelector('.empty-dash h2:has-text("Add your first card")');
  check('an empty dashboard offers the four kinds of card right there', (await page.locator('.empty-dash .type-tile').count()) === 4);
  check('time controls are always there, monthly over the last 12 months by default',
    (await page.locator('.range-name').innerText()) === 'Last 12 months'
    && (await page.locator('.segmented[aria-label="Group by time period"] button.on').innerText()) === 'Month');
  await shot('7-empty-dashboard');

  // 9. add a line chart: pick the kind, pick a table, it is pre-filled and already run
  await page.locator('.empty-dash .type-tile:has-text("Line chart")').click();
  await page.waitForSelector('h1:has-text("Add a line chart to Sales overview")');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.orders');
  await page.waitForSelector('path.recharts-line-curve');
  check('choosing a table pre-fills a count per month and runs it',
    (await page.locator('select[aria-label="Group by column"]').inputValue()).includes('created_at')
    && (await page.locator('select[aria-label="Group dates by"]').inputValue()) === 'month');
  await shot('8-add-line');
  check('the name is suggested from the question', (await page.locator('.save input').inputValue()) === 'Orders per month', await page.locator('.save input').inputValue());
  await page.click('button:has-text("Add to dashboard")');
  await page.waitForSelector('.timebar');
  await page.waitForSelector('.dash-card path.recharts-line-curve');
  check('the card lands on the dashboard, drawn as a line', (await page.locator('.dash-card h3:has-text("Orders per month")').count()) === 1);
  check('and says which date column the range applies to', (await page.locator('.dash-card').innerText()).includes('by orders.created_at'));

  // 10. bar, pie and table cards through the Add card button
  await addCardFromTopButton('Bar chart');
  await page.waitForSelector('h1:has-text("Add a bar chart")');
  await pickTableAndAdd('users', '.recharts-bar-rectangle', 'Users by country');
  await page.waitForSelector('.dash-card:has-text("Users by country") .recharts-bar-rectangle');

  await addCardFromTopButton('Pie chart');
  await pickTableAndAdd('orders', '.recharts-pie-sector', 'Orders by status');
  await page.waitForSelector('.dash-card:has-text("Orders by status") .recharts-pie-sector');

  await addCardFromTopButton('Table');
  await pickTableAndAdd('legacy_events', 'table.grid', 'Legacy events');
  await page.waitForSelector('.dash-card:has-text("Legacy events") table.grid');
  check('a table card uses the table\'s own date column', (await page.locator('.dash-card:has-text("Legacy events")').innerText()).includes('by legacy_events.happened_at'));

  await addCardFromTopButton('Table');
  await pickTableAndAdd('order_items', 'table.grid', null);
  await settled();
  check('a table with no date column says so instead of pretending',
    (await page.locator('.dash-card:has-text("Order items") .chip:has-text("no date field")').count()) === 1);
  check('five cards on the dashboard', (await page.locator('.dash-card').count()) === 5);
  await shot('9-dashboard-five');

  // a table with no date column cannot make a line chart: it explains
  await addCardFromTopButton('Line chart');
  await page.selectOption('select[aria-label="Table to start from"]', 'public.categories');
  await page.waitForSelector('.banner.warn');
  check('a line chart on a table without dates explains why it cannot start', /no date column/.test(await page.locator('.banner.warn').innerText()));
  await page.click('button:has-text("← Back to Sales overview")');
  await page.waitForSelector('.dash-card');
  check('going back without saving adds nothing', (await page.locator('.dash-card').count()) === 5);

  // 11. time controls reach every card
  const monthly = await cardRows('Orders per month');
  check('last 12 months by month gives about 12 points', monthly >= 11 && monthly <= 13, `rows=${monthly}`);
  const pieTotal = async () => (await page.locator('.dash-card:has-text("Orders by status") .legend-value').allInnerTexts()).reduce((s, v) => s + Number(v.replace(/,/g, '')), 0);
  const pieAll12 = await pieTotal();

  const gran = (label) => page.locator(`.segmented[aria-label="Group by time period"] button:has-text("${label}")`).click();
  await gran('Week'); await settled();
  const weekly = await cardRows('Orders per month');
  check('Week re-buckets the line chart', weekly >= 44 && weekly <= 56, `rows=${weekly}`);
  await gran('Day'); await settled();
  const daily = await cardRows('Orders per month');
  check('Day gives a point per day', daily > 200, `rows=${daily}`);
  await gran('Year'); await settled();
  check('Year gives a point per year', (await cardRows('Orders per month')) <= 2);
  await gran('Month'); await settled();

  await page.click('.range-button');
  await page.locator('.preset-row:has-text("Last 30 days")').click();
  await settled();
  const monthly30 = await cardRows('Orders per month');
  check('Last 30 days shrinks the line chart to the last month or two', monthly30 >= 1 && monthly30 <= 2, `rows=${monthly30}`);
  const pie30 = await pieTotal();
  check('and the pie on the same dashboard follows the same range', pie30 > 0 && pie30 < pieAll12, `${pie30} < ${pieAll12}`);
  check('the range button says what is selected', (await page.locator('.range-name').innerText()) === 'Last 30 days');
  await shot('10-last-30-days');

  await page.click('.range-button');
  await page.locator('.preset-row:has-text("All time")').click();
  await settled();
  check('All time brings every month back', (await cardRows('Orders per month')) >= 12);
  check('and every order back in the pie', (await pieTotal()) === 2000, String(await pieTotal()));

  await page.click('.range-button');
  const bad = page.locator('.popover-foot');
  await bad.locator('input[type=date]').nth(0).fill('2026-06-30');
  await bad.locator('input[type=date]').nth(1).fill('2026-06-01');
  await bad.locator('button:has-text("Apply")').click();
  check('a backwards custom range is refused with a message',
    (await page.locator('.popover-foot .field-error').count()) === 1
    && /after the end date/.test(await page.locator('.popover-foot .field-error').innerText())
    && (await page.locator('.range-name').innerText()) === 'All time');
  await page.keyboard.press('Escape');
  await page.click('.range-button');
  await page.locator('.popover-foot input[type=date]').nth(0).fill('2026-06-01');
  await page.locator('.popover-foot input[type=date]').nth(1).fill('2026-06-30');
  await page.locator('.popover-foot button:has-text("Apply")').click();
  await settled();
  check('a custom range applies', (await page.locator('.range-name').innerText()) === 'Custom range' && (await pieTotal()) > 0 && (await pieTotal()) < 2000);
  await shot('11-custom-range');

  // the choice is remembered for this dashboard
  await page.reload();
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.dash-card');
  check('the date range is remembered after a reload', (await page.locator('.range-name').innerText()) === 'Custom range');

  // 12. edit a card from the dashboard and come back
  await page.locator('.dash-card:has-text("Users by country") button:has-text("Edit")').click();
  await page.waitForSelector('h1:has-text("Edit card on Sales overview")');
  await page.fill('.save input', 'Customers by country');
  await page.click('button:has-text("Save changes")');
  await page.waitForSelector('.timebar');
  await page.waitForSelector('.dash-card h3:has-text("Customers by country")');
  check('editing a card returns to the dashboard with the change', (await page.locator('.dash-card').count()) === 5);

  // 13. add a question saved earlier, remove a card
  await page.click('button:has-text("+ Add card")');
  await page.waitForSelector('.dialog');
  await page.selectOption('select[aria-label="A saved question"]', { label: 'Orders by country' });
  await page.click('button:has-text("Add it")');
  await page.waitForSelector('.dash-card:has-text("Orders by country") .recharts-bar-rectangle');
  check('a saved question can be added from the same dialog', (await page.locator('.dash-card').count()) === 6);
  await page.locator('.dash-card:has-text("Legacy events") button[aria-label^="Remove"]').click();
  await page.waitForFunction(() => document.querySelectorAll('.dash-card').length === 5);
  check('removing a card takes it off the dashboard', true);

  // 14. a second dashboard, then delete it
  await page.click('button:has-text("+ New dashboard")');
  await page.fill('#dash-name', 'Support');
  await page.click('button:has-text("Create")');
  await page.waitForSelector('.empty-dash h2:has-text("Add your first card")');
  check('a second dashboard starts empty, with the controls at their defaults', (await page.locator('.range-name').innerText()) === 'Last 12 months');
  await page.click('.menu summary');
  await page.click('.menu-items button:has-text("Delete dashboard")');
  await page.click('.menu-items button:has-text("Yes, delete")');
  await page.waitForSelector('.dash-card');
  check('deleting a dashboard goes back to the other one, questions untouched', (await page.locator('.dash-title option').count()) === 1);

  // 15. the library still has everything, and deleting a question takes it off dashboards
  await page.click('.nav-item:has-text("Saved questions")');
  await page.waitForSelector('.list-card');
  const names = await page.locator('.list-card .strong').allInnerTexts();
  check('saved questions lists every card that was made', ['Orders per month', 'Customers by country', 'Orders by status', 'Legacy events', 'Order items', 'Orders by country'].every(n => names.includes(n)), JSON.stringify(names));
  await page.locator('.list-card:has-text("Orders by country") button:has-text("Open")').click();
  await page.waitForSelector('.result');
  check('opening a saved question restores the form and runs it',
    (await page.locator('select[aria-label="Table to start from"]').inputValue()) === 'public.orders'
    && (await page.locator('h1').first().innerText()) === 'Edit question');
  await page.click('.nav-item:has-text("Saved questions")');
  await page.waitForSelector('.list-card');
  const before = await page.locator('.list-card').count();
  await page.locator('.list-card:has-text("Orders by country") button:has-text("Delete")').click();
  await page.locator('button:has-text("Yes, delete")').click();
  await page.waitForFunction((n) => document.querySelectorAll('.list-card').length === n - 1, before);
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.dash-card');
  check('deleting a question removes its card from the dashboard', (await page.locator('.dash-card:has-text("Orders by country")').count()) === 0);

  console.log('\nconsole/page errors:', problems.length ? problems : 'none');
  console.log(`${results.filter(Boolean).length}/${results.length} checks passed`);
  await browser.close();
  process.exit(results.every(Boolean) && problems.length === 0 ? 0 : 1);
})().catch(e => { console.error('SCRIPT FAILED:', e.message.split('\n')[0]); process.exit(2); });
