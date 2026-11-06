# DataLens

A lightweight database explorer and analytics tool, inspired by Metabase. Connect a PostgreSQL
database with a read-only connection string, learn its structure, build queries without writing SQL, chart
the results, and keep them on dashboards. See [`docs/design.md`](docs/design.md) for the design.

## Status

| Step | State |
|------|-------|
| Connect with one connection string, test it, warn if the user can write | done |
| Sync: read tables, columns, keys, foreign keys, indexes into a snapshot | done |
| App database (SQLite) for saved connections (encrypted) and snapshots | done |
| Data explorer: browse any table with paging and sorting, follow relationships | done |
| Safe executor: read-only transaction, timeout, row cap | done |
| Web UI (React) and API | done |
| Query builder: filters, group by, summaries, joins along foreign keys, sorting; live SQL | done |
| Charts: bar, line, pie, and a table view of every result | done |
| Saved questions and dashboards (questions re-run live, results are never stored) | done |
| Login, more database engines, deployment image | not started |

## Try it

You need the .NET 10 SDK and Docker.

```bash
docker compose up -d                  # sample "shop" database on localhost:5433
```

**In the browser.** Needs Node.js 20+ once, to build the UI.

```bash
./scripts/run.sh
```

Open <http://localhost:5080> and paste `postgresql://datalens_ro:readonly@localhost:5433/shop`. Then:

- **Dashboards** (the home screen). On a fresh install, **Load the sample shop dashboard** builds a full board of
  twelve cards in one click (or run `datalens demo shop`). **+ Add card** opens the question builder in a pop-up:
  choose Number, Line chart, Bar chart, Donut chart or Table, pick a table, and it is pre-filled and already run.
  Press **Add to dashboard**.
  - **Number** shows a headline figure (13.98K); a question with several figures in one row becomes a row of them.
  - **Date row** above the cards: quick picks (Today, Yesterday, 7D, 30D, 3M, 6M, 12M, YTD, All), a custom range,
    and the period (day, week, month, quarter, year). They apply to every card and are remembered per dashboard.
  - **Several tables in one chart**: add related tables (joined along foreign keys, no `ON` clause to write), up to
    six. The sample board has three-table and five-table charts.
  - Pick related tables, filters, summaries and groupings; **Show SQL** displays exactly what will run. Filter
    values are suggested from what really exists in the column as you type, and a misspelling still finds the
    right value ("shiped" offers "shipped").
  - **Edit** on a card reopens its question in the pop-up.
- **Saved questions** live in the Add card pop-up: reuse one on another dashboard, or delete it.
- **Browse data**: any table, page by page. Click a column header to sort, a `→ users` chip to follow a
  relationship, or **Ask a question about this** to open the builder (it can add the result to a dashboard as it
  saves).

The API listens on this machine only.

**From the terminal.**

```bash
dotnet run --project src/DataLens.Cli -- connect shop \
  "postgresql://datalens_ro:readonly@localhost:5433/shop"
```

```bash
dotnet run --project src/DataLens.Cli -- show shop
```

```bash
dotnet run --project src/DataLens.Cli -- rows shop public.orders --sort created_at --desc --size 10
```

Other commands: `demo <name>` (the sample dashboard), `sync <name>`, `list`, `show <name> --json`.
Connection names match whatever the case.

**Where the data lives.** The web app and the CLI share one folder, `.datalens/` at the repository root, holding the
app database and the local encryption key. Wherever you start either from, they find the same one (it walks up to
`DataLens.sln`). The web app prints it when it starts and `list` prints it first, so a "no saved connection" is
easy to explain. Set `DATALENS_HOME` to use another folder, and `DATALENS_KEY` (base64, 32 bytes) to supply your own key.

Connect as `shop_admin` / `admin` instead to see the write-access warning.
`legacy_events` in the sample data has a `user_id` column with no declared foreign key, to show that
sync only reports relationships the database actually declares.

## Tests

```bash
dotnet test
```

The Postgres tests are skipped unless these are set (values for the sample container):

```bash
export DATALENS_TEST_PG_RO="Host=localhost;Port=5433;Database=shop;Username=datalens_ro;Password=readonly"
export DATALENS_TEST_PG_RW="Host=localhost;Port=5433;Database=shop;Username=shop_admin;Password=admin"
dotnet test
```

### Windows notes

- In PowerShell set variables with `$env:DATALENS_TEST_PG_RO = "..."`, not `export`.
- WSL works well: install the .NET 10 SDK inside WSL, enable Docker Desktop's WSL integration, and clone the
  repo inside the WSL filesystem (`~/`), not under `/mnt/d`, for speed. Delete any `bin/` and `obj/`
  folders that a Windows build left behind.

## Layout

```
DataLens.sln
docker-compose.yml          sample source database now; the app joins it at deploy time
db/init/01-seed.sql         sample "shop" data and the read-only role
docs/design.md              architecture and decisions
src/
  DataLens.Core/            models and interfaces. No dependencies. Everything else points here.
  DataLens.Infrastructure/  Postgres connector (Npgsql), SQLite app store, encryption
  DataLens.Cli/             command line front end
  DataLens.Api/             ASP.NET Core API; also serves the built UI from wwwroot
web/                        React + TypeScript UI (Vite). `npm run build` writes into DataLens.Api/wwwroot
scripts/run.sh              build the UI and start the API
scripts/e2e.cjs             optional browser walkthrough of the whole UI (needs Playwright)
tests/
  DataLens.Tests/           xUnit; unit tests plus Postgres integration tests
```

Dependency direction: `Cli` and `Api` depend on `Infrastructure`, which depends on `Core`.
`Core` depends on nothing, so the query model and compiler stay pure and easy to test.

## Deployment plan

One container. The React app is built and copied into the API's `wwwroot`, so ASP.NET Core serves both
the API and the UI. SQLite lives on a mounted volume. `docker compose up` then runs the app next to the
sample database.
