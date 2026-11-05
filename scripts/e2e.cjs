// Optional end-to-end check: drives the real UI in a browser: connect, build questions in the pop-up, and a
// dashboard with every kind of card, its date range and its granularity controls.
// Needs: the sample database running (docker compose up -d), a Playwright install, and a FRESH data folder.
//   cd web && npm install --no-save playwright && npx playwright install chromium
//   cd .. && DATALENS_HOME=$(mktemp -d) dotnet run --project src/DataLens.Api &
//   node scripts/e2e.cjs <folder for screenshots>
// Exits 0 only if every check passed and the browser logged no errors.
// (Some checks assume the sample data's dates are recent, as they are right after the database is created.)
const { chromium } = require('playwright');
const SP = process.argv[2] || '.';
const URL = 'http://localhost:5080';
const B = '.dialog-xl';
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

  // ---- helpers ----------------------------------------------------------------------------
  const openStandalone = async () => {
    await page.click('.nav-item:has-text("Browse data")');
    await page.waitForSelector('.browse table.grid tbody tr');
    await page.click('button:has-text("Ask a question about this")');
    await page.waitForSelector(`${B} .builder`);
  };
  const closeBuilder = async () => {
    await page.click(`${B} button[aria-label="Close"]`);
    await page.waitForSelector(B, { state: 'detached' });
  };
  const runAndWait = async () => {
    await page.click(`${B} button:has-text("Run")`);
    await page.waitForFunction(() => document.querySelector('.dialog-xl .result') && !document.querySelector('.dialog-xl button.primary')?.textContent.includes('Running'));
  };
  const ready = () => page.waitForFunction(() => !document.querySelector('.dialog-xl .builder-actions button.primary').disabled);
  const settled = () => page.waitForFunction(() => !document.querySelector('.dash-card.loading'));
  const cardRows = async (name) => Number((await page.locator(`.dash-card:has-text("${name}") footer`).innerText()).match(/([\d,]+) rows/)[1].replace(/,/g, ''));
  const pieTotal = async () => (await page.locator('.dash-card:has-text("Orders by status") .legend-value').allInnerTexts()).reduce((s, v) => s + Number(v.replace(/,/g, '')), 0);

  // ======================= connect, and the shape of the app ===================================
  await page.goto(URL);
  await page.waitForSelector('form.connect');
  await page.fill('#conn-name', 'shop');
  await page.fill('#conn-string', 'postgresql://datalens_ro:readonly@127.0.0.1:5433/shop');
  await page.click('button:has-text("Connect and read structure")');
  await page.waitForSelector('.empty-dash h2:has-text("Create your first dashboard")');
  check('after connecting you land on Dashboards', true);
  const nav = await page.locator('.nav-item').allInnerTexts();
  check('the sidebar has only Dashboards and Browse data', JSON.stringify(nav) === JSON.stringify(['Dashboards', 'Browse data']), JSON.stringify(nav));

  // ======================= the builder, as a pop-up over Browse ================================
  await openStandalone();
  check('the question builder opens as a pop-up over the page', (await page.locator('.browse').count()) === 1 && (await page.locator(B).count()) === 1);
  await page.keyboard.press('Escape');
  await page.mouse.click(5, 5);   // the backdrop
  check('Escape and clicking outside do not throw away a half-built question', (await page.locator(B).count()) === 1);

  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.orders');
  await page.click(`${B} button:has-text("+ Add a summary")`);
  await page.click(`${B} button:has-text("+ Group by")`);
  await ready();
  await page.click(`${B} button:has-text("Show SQL")`);
  const sql1 = await page.locator(`${B} pre.sql`).innerText();
  check('live SQL shows date_trunc month + COUNT + GROUP BY', /date_trunc\('month'/.test(sql1) && /COUNT\(\*\)/.test(sql1) && /GROUP BY 1/.test(sql1));
  await runAndWait();
  check('suggests a line chart for dates + a count', await page.locator(`${B} .segmented button.on`).innerText() === 'Line');
  check('line is drawn with dots', (await page.locator(`${B} path.recharts-line-curve`).count()) === 1 && (await page.locator(`${B} .recharts-line-dots circle, ${B} .recharts-line-dot`).count()) >= 6);
  const ticks = (await page.locator(`${B} .recharts-cartesian-axis-tick-value`).evaluateAll(els => els.map(e => e.textContent))).filter(t => /^[A-Z][a-z]{2} \d{4}$/.test(t));
  const dates = ticks.map(t => new Date('1 ' + t.replace(/\s+/g, ' ')).getTime());
  check('the line runs oldest to newest with no sort chosen', dates.length >= 4 && dates.every((d, i) => i === 0 || d > dates[i - 1]), JSON.stringify(ticks));
  check('shows row count and timing', /\d+ rows? · \d+ ms/.test(await page.locator(`${B} .builder-actions .small`).innerText()));
  await shot('1-builder-popup');

  check('default order is shown in the SQL', /ORDER BY 1 ASC NULLS LAST/.test(await page.locator(`${B} pre.sql`).innerText()));
  await page.selectOption(`${B} select[aria-label="Sort by"]`, 'created_at');
  await page.selectOption(`${B} select[aria-label="Sort direction"]`, 'desc');
  await page.waitForFunction(() => document.querySelector('.dialog-xl pre.sql').textContent.includes('"created_at" DESC'));
  check('choosing a sort replaces the default', /ORDER BY "created_at" DESC/.test(await page.locator(`${B} pre.sql`).innerText()) && !/ORDER BY 1/.test(await page.locator(`${B} pre.sql`).innerText()));
  await page.selectOption(`${B} select[aria-label="Sort by"]`, '');
  await page.waitForFunction(() => document.querySelector('.dialog-xl pre.sql').textContent.includes('ORDER BY 1'));

  await page.click(`${B} button:has-text("+ Add a filter")`);
  await page.locator(`${B} select[aria-label="Filter column"]`).first().selectOption({ label: 'status' });
  await page.locator(`${B} select[aria-label="Filter condition"]`).first().selectOption('eq');
  // the value box suggests what really exists in the column
  const value = page.locator(`${B} input[aria-label="Value"]`).first();
  const options = async () => (await page.locator(`${B} [role="option"]`).allInnerTexts()).map(t => t.replace(/^✓\s*/, '').trim());
  // waits until the list shows exactly these, rather than reading it while a debounced search is still on its way
  const optionsAre = (expected) => page.waitForFunction((exp) => {
    const shown = [...document.querySelectorAll('.dialog-xl [role="option"]')].map(e => e.textContent.replace(/^[\s✓]+/, '').trim());
    return JSON.stringify(shown) === JSON.stringify(exp);
  }, expected, { timeout: 5000 }).then(() => true, () => false);
  await value.focus();
  check('focusing the value box lists the values that exist in the column', await optionsAre(['paid', 'pending', 'refunded', 'shipped']));
  await shot('suggestions-open');
  await value.fill('ing');
  check('typing narrows the list, matching inside a value', await optionsAre(['pending']));
  await value.fill('shiped');
  await page.waitForSelector(`${B} .combo-heading`);
  check('a misspelling offers the right value under "Did you mean"',
    (await optionsAre(['shipped'])) && /did you mean/i.test(await page.locator(`${B} .combo-heading`).innerText()));
  await shot('suggestions-typo');
  await value.fill('zzzzzz');
  await page.waitForSelector(`${B} .combo-note:has-text("Nothing like that")`);
  check('with nothing close, it says so and still lets you use what you typed', (await options()).length === 0);
  await value.fill('pa');
  check('a shorter search finds the start of a value', await optionsAre(['paid']));
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  check('arrow keys and Enter pick a suggestion and close the list',
    (await value.inputValue()) === 'paid' && (await page.locator(`${B} [role="listbox"]`).count()) === 0);
  await value.click();
  await page.waitForSelector(`${B} [role="listbox"]`);
  await page.keyboard.press('Escape');
  check('Escape closes the list but not the pop-up', (await page.locator(`${B} [role="listbox"]`).count()) === 0 && (await page.locator(B).count()) === 1);
  check('the typed value stays', (await value.inputValue()) === 'paid');
  // the SQL follows the box after a short delay: wait for it to catch up with "paid", not an earlier keystroke
  await page.waitForFunction(() => document.querySelector('.dialog-xl .params')?.textContent.includes('paid'));
  const sqlF = await page.locator(`${B} pre.sql`).innerText();
  check('a filter value is a parameter, not part of the SQL', /"t0"\."status" = @p1/.test(sqlF) && !/paid/.test(sqlF) && (await page.locator(`${B} .params`).innerText()).includes('paid'));
  await page.click(`${B} button:has-text("+ Add a filter")`);
  await page.locator(`${B} select[aria-label="Filter column"]`).nth(1).selectOption({ label: 'user_id' });
  await page.locator(`${B} select[aria-label="Filter condition"]`).nth(1).selectOption('in');
  const second = page.locator(`${B} input[aria-label="Value"]`).nth(1);
  await second.focus();
  await page.waitForSelector(`${B} [role="option"]`);
  check('a number column suggests the numbers in it', (await options()).slice(0, 3).join(',') === '1,2,3');
  await page.locator(`${B} [role="option"]`).filter({ hasText: /^\s*1\s*$/ }).first().click();
  await page.locator(`${B} [role="option"]`).filter({ hasText: /^\s*2\s*$/ }).first().click();
  check('"is one of" collects several values', (await second.inputValue()).replace(/[,\s]+$/, '') === '1, 2');
  check('and marks the picked ones', (await page.locator(`${B} [role="option"][aria-selected="true"]`).count()) === 2);
  await page.locator(`${B} [role="option"][aria-selected="true"]`).first().click();
  check('picking one again takes it out', (await second.inputValue()).replace(/[,\s]+$/, '') === '2');
  await page.locator(`${B} button[aria-label="Remove filter"]`).nth(1).click();
  await runAndWait();
  await page.click(`${B} .segmented button:has-text("Bar")`);
  await page.waitForSelector(`${B} .recharts-bar-rectangle`);
  check('a month axis stays as columns, with bars at most 24px thick',
    await page.locator(`${B} .recharts-bar-rectangle path`).first().evaluate(el => { const b = el.getBBox(); return Math.min(b.width, b.height) <= 24.5 && b.height > b.width; }));

  // a pie from a second question
  await closeBuilder(); await openStandalone();
  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.orders');
  await page.click(`${B} button:has-text("+ Add a summary")`);
  await page.click(`${B} button:has-text("+ Group by")`);
  await page.selectOption(`${B} select[aria-label="Group by column"]`, { label: 'status' });
  await ready(); await runAndWait();
  check('text + count suggests a bar chart', await page.locator(`${B} .segmented button.on`).innerText() === 'Bar');
  await page.click(`${B} .segmented button:has-text("Pie")`);
  await page.waitForSelector(`${B} .recharts-pie-sector`);
  const legend = (await page.locator(`${B} .pie-legend`).innerText()).replace(/\s+/g, ' ');
  check('pie has 4 slices and a legend with names, amounts and shares', (await page.locator(`${B} .recharts-pie-sector`).count()) === 4 && /paid/.test(legend) && /%/.test(legend), legend);
  await page.click(`${B} .segmented button:has-text("Table")`);
  check('the table view of the same result has 4 rows', (await page.locator(`${B} .result table.grid tbody tr`).count()) === 4);

  // a join through a foreign key
  await closeBuilder(); await openStandalone();
  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.orders');
  const related = await page.locator(`${B} select[aria-label="Add a related table"] option`).allInnerTexts();
  check('offers tables linked by foreign keys', related.some(t => /users, through orders\.user_id/.test(t)) && related.some(t => /order_items, through order_items\.order_id/.test(t)));
  await page.selectOption(`${B} select[aria-label="Add a related table"]`, { label: related.find(t => /^users/.test(t)) });
  await page.click(`${B} button:has-text("+ Add a summary")`);
  await page.click(`${B} button:has-text("+ Group by")`);
  await page.selectOption(`${B} select[aria-label="Group by column"]`, { label: 'users.country' });
  await ready();
  if (!(await page.locator(`${B} pre.sql`).count())) await page.click(`${B} button:has-text("Show SQL")`);
  check('the ON clause is written for the user', /LEFT JOIN "public"\."users" AS "t1" ON "t0"\."user_id" = "t1"\."id"/.test(await page.locator(`${B} pre.sql`).innerText()));
  await runAndWait();
  await page.click(`${B} .segmented button:has-text("Table")`);
  const cells = await page.locator(`${B} .result table.grid tbody tr td:last-child`).allInnerTexts();
  check('orders per country add up to all 2000 orders', cells.reduce((s, c) => s + Number(c.replace(/,/g, '')), 0) === 2000);
  await page.click(`${B} .segmented button:has-text("Bar")`);
  await page.waitForSelector(`${B} .recharts-bar-rectangle`);

  // save it from Browse: it goes to the library, and the pop-up closes with a note
  await page.fill(`${B} .save input`, 'Orders by country');
  check('with no dashboards yet there is no "also add to" menu', (await page.locator(`${B} select[aria-label="Also add to a dashboard"]`).count()) === 0);
  await page.click(`${B} button:has-text("Save question")`);
  await page.waitForSelector(B, { state: 'detached' });
  check('saving closes the pop-up and says where the question went', /Saved “Orders by country”/.test(await page.locator('.banner.ok').innerText()));

  // ======================= dashboards: the add-card flow ======================================
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.empty-dash h2:has-text("Create your first dashboard")');
  await shot('2-first-dashboard');
  await page.fill('#dash-name', 'Sales overview');
  await page.click('button:has-text("Create")');
  await page.waitForSelector('.empty-dash h2:has-text("Add your first card")');
  check('an empty dashboard offers the four kinds of card right there', (await page.locator('.empty-dash .type-tile').count()) === 4);
  check('time controls are always there, monthly over the last 12 months by default',
    (await page.locator('.range-name').innerText()) === 'Last 12 months'
    && (await page.locator('.segmented[aria-label="Group by time period"] button.on').innerText()) === 'Month');

  const pickTableAndAdd = async (table, expectChart, nameExpect) => {
    await page.selectOption(`${B} select[aria-label="Table to start from"]`, `public.${table}`);
    await page.waitForSelector(`${B} .result`);
    if (expectChart) await page.waitForSelector(`${B} ${expectChart}`);
    if (nameExpect) check(`suggests the name "${nameExpect}"`, (await page.locator(`${B} .save input`).inputValue()) === nameExpect, await page.locator(`${B} .save input`).inputValue());
    await page.click(`${B} button:has-text("Add to dashboard")`);
    await page.waitForSelector(B, { state: 'detached' });
  };
  const addCardFromTopButton = async (kind) => {
    await page.click('button:has-text("+ Add card")');
    await page.waitForSelector('.dialog');
    await page.locator(`.dialog .type-tile:has-text("${kind}")`).click();
    await page.waitForSelector(`${B} .builder`);
  };

  await page.locator('.empty-dash .type-tile:has-text("Line chart")').click();
  await page.waitForSelector(`${B} h1:has-text("Add a line chart to Sales overview")`);
  check('the builder pops up over the dashboard, which stays where it was', (await page.locator('.timebar').count()) === 1);
  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.orders');
  await page.waitForSelector(`${B} path.recharts-line-curve`);
  check('choosing a table pre-fills a count per month and runs it',
    (await page.locator(`${B} select[aria-label="Group by column"]`).inputValue()).includes('created_at')
    && (await page.locator(`${B} select[aria-label="Group dates by"]`).inputValue()) === 'month');
  await shot('3-add-line-popup');
  check('the name is suggested from the question', (await page.locator(`${B} .save input`).inputValue()) === 'Orders per month');
  await page.click(`${B} button:has-text("Add to dashboard")`);
  await page.waitForSelector(B, { state: 'detached' });
  await page.waitForSelector('.dash-card path.recharts-line-curve');
  check('the card lands on the dashboard, drawn as a line, and says which date column the range uses',
    (await page.locator('.dash-card h3:has-text("Orders per month")').count()) === 1 && (await page.locator('.dash-card').innerText()).includes('by orders.created_at'));

  await addCardFromTopButton('Bar chart');
  await pickTableAndAdd('users', '.recharts-bar-rectangle', 'Users by country');
  await page.waitForSelector('.dash-card:has-text("Users by country") .recharts-bar-rectangle');
  await addCardFromTopButton('Pie chart');
  await pickTableAndAdd('orders', '.recharts-pie-sector', 'Orders by status');
  await addCardFromTopButton('Table');
  await pickTableAndAdd('legacy_events', 'table.grid', 'Legacy events');
  await page.waitForSelector('.dash-card:has-text("Legacy events") table.grid');
  check('a table card uses the table\'s own date column', (await page.locator('.dash-card:has-text("Legacy events")').innerText()).includes('by legacy_events.happened_at'));
  await addCardFromTopButton('Table');
  await pickTableAndAdd('order_items', 'table.grid', null);
  await page.waitForSelector('.dash-card:has-text("Order items") footer');   // its first result has arrived
  await settled();
  check('a table with no date column says so instead of pretending', (await page.locator('.dash-card:has-text("Order items") .chip:has-text("no date field")').count()) === 1);
  check('five cards on the dashboard', (await page.locator('.dash-card').count()) === 5);
  await shot('4-dashboard-five');

  await addCardFromTopButton('Line chart');
  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.categories');
  await page.waitForSelector(`${B} .banner.warn`);
  check('a line chart on a table without dates explains why it cannot start', /no date column/.test(await page.locator(`${B} .banner.warn`).innerText()));
  await closeBuilder();
  check('closing the pop-up adds nothing', (await page.locator('.dash-card').count()) === 5);

  // ======================= time controls reach every card =====================================
  const monthly = await cardRows('Orders per month');
  check('last 12 months by month gives about 12 points', monthly >= 11 && monthly <= 13, `rows=${monthly}`);
  const pieAll12 = await pieTotal();
  const gran = (label) => page.locator(`.segmented[aria-label="Group by time period"] button:has-text("${label}")`).click();
  await gran('Week'); await settled();
  const weekly = await cardRows('Orders per month');
  check('Week re-buckets the line chart', weekly >= 44 && weekly <= 56, `rows=${weekly}`);
  await gran('Day'); await settled();
  check('Day gives a point per day', (await cardRows('Orders per month')) > 200);
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
  await shot('5-last-30-days');
  await page.click('.range-button');
  await page.locator('.preset-row:has-text("All time")').click();
  await settled();
  check('All time brings every order back in the pie', (await pieTotal()) === 2000);

  await page.click('.range-button');
  await page.locator('.popover-foot input[type=date]').nth(0).fill('2026-06-30');
  await page.locator('.popover-foot input[type=date]').nth(1).fill('2026-06-01');
  await page.locator('.popover-foot button:has-text("Apply")').click();
  check('a backwards custom range is refused with a message',
    /after the end date/.test(await page.locator('.popover-foot .field-error').innerText()) && (await page.locator('.range-name').innerText()) === 'All time');
  await page.keyboard.press('Escape');
  await page.click('.range-button');
  await page.locator('.popover-foot input[type=date]').nth(0).fill('2026-06-01');
  await page.locator('.popover-foot input[type=date]').nth(1).fill('2026-06-30');
  await page.locator('.popover-foot button:has-text("Apply")').click();
  await settled();
  check('a custom range applies', (await page.locator('.range-name').innerText()) === 'Custom range' && (await pieTotal()) > 0 && (await pieTotal()) < 2000);
  await page.reload();
  await page.waitForSelector('.dash-card');
  check('the date range is remembered after a reload', (await page.locator('.range-name').innerText()) === 'Custom range');

  // ======================= edit, reuse, remove, delete ========================================
  await page.locator('.dash-card:has-text("Users by country") button:has-text("Edit")').click();
  await page.waitForSelector(`${B} h1:has-text("Edit card on Sales overview")`);
  await page.fill(`${B} .save input`, 'Customers by country');
  await page.click(`${B} button:has-text("Save changes")`);
  await page.waitForSelector(B, { state: 'detached' });
  await page.waitForSelector('.dash-card h3:has-text("Customers by country")');
  check('editing a card in the pop-up changes it in place', (await page.locator('.dash-card').count()) === 5);

  await page.click('button:has-text("+ Add card")');
  await page.waitForSelector('.dialog');
  check('saved questions are listed right in the Add card dialog', (await page.locator('.dialog .saved-row:has-text("Orders by country")').count()) === 1);
  check('questions already on the dashboard are marked', (await page.locator('.dialog .saved-row:has-text("Orders per month") button:has-text("On dashboard")').count()) === 1);
  await page.locator('.dialog .saved-row:has-text("Orders by country") button:has-text("Add")').click();
  await page.waitForSelector('.dash-card:has-text("Orders by country") .recharts-bar-rectangle');
  check('a saved question can be added from that list', (await page.locator('.dash-card').count()) === 6);

  await page.locator('.dash-card:has-text("Legacy events") button[aria-label^="Remove"]').click();
  await page.waitForFunction(() => document.querySelectorAll('.dash-card').length === 5);
  check('removing a card takes it off the dashboard', true);

  // a question from Browse can be put on a dashboard as it is saved
  await openStandalone();
  await page.selectOption(`${B} select[aria-label="Table to start from"]`, 'public.categories');
  await ready(); await runAndWait();
  check('with a dashboard to add to, the save row offers it', (await page.locator(`${B} select[aria-label="Also add to a dashboard"]`).count()) === 1);
  await page.selectOption(`${B} select[aria-label="Also add to a dashboard"]`, { label: 'Sales overview' });
  await page.click(`${B} button:has-text("Save question")`);
  await page.waitForSelector(B, { state: 'detached' });
  check('and the note says it was added', /added it to Sales overview/.test(await page.locator('.banner.ok').innerText()));
  await page.click('.nav-item:has-text("Dashboards")');
  await page.waitForSelector('.dash-card:has-text("Categories")');
  check('the new card is on the dashboard', (await page.locator('.dash-card:has-text("Categories")').count()) === 1);

  // deleting a saved question removes its card
  await page.click('button:has-text("+ Add card")');
  await page.waitForSelector('.dialog');
  await page.locator('.dialog .saved-row:has-text("Orders by country") button:has-text("Delete")').click();
  check('deleting a saved question asks first and warns about its card', /and its card/.test(await page.locator('.dialog .saved-row:has-text("Orders by country")').innerText()));
  await page.locator('.dialog .saved-row:has-text("Orders by country") button:has-text("Yes")').click();
  await page.waitForFunction(() => !document.body.innerText.includes('Orders by country'));
  check('the question and its card are both gone', (await page.locator('.dialog .saved-row:has-text("Orders by country")').count()) === 0 && (await page.locator('.dash-card:has-text("Orders by country")').count()) === 0);
  await page.keyboard.press('Escape');

  // a second dashboard, then delete it
  await page.click('button:has-text("+ New dashboard")');
  await page.fill('#dash-name', 'Support');
  await page.click('button:has-text("Create")');
  await page.waitForSelector('.empty-dash h2:has-text("Add your first card")');
  check('a second dashboard starts empty, with the controls at their defaults', (await page.locator('.range-name').innerText()) === 'Last 12 months');
  await page.click('.menu summary');
  await page.click('.menu-items button:has-text("Delete dashboard")');
  await page.click('.menu-items button:has-text("Yes, delete")');
  await page.waitForSelector('.dash-card');
  check('deleting a dashboard goes back to the other one', (await page.locator('.dash-title option').count()) === 1);

  console.log('\nconsole/page errors:', problems.length ? problems : 'none');
  console.log(`${results.filter(Boolean).length}/${results.length} checks passed`);
  await browser.close();
  process.exit(results.every(Boolean) && problems.length === 0 ? 0 : 1);
})().catch(e => { console.error('SCRIPT FAILED:', e.message.split('\n')[0]); process.exit(2); });
