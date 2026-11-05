# DataLens: Design (draft)

A lightweight BI tool: connect a PostgreSQL database, discover its schema, build queries without writing SQL, run them live, and chart the results.

## Core idea

Two databases:

- **Source DB**: the user's data. Read only. We never copy it.
- **App DB** (SQLite): our own state: encrypted connection, schema snapshot, saved queries, dashboards.

Every chart is a live query against the source DB at request time.

## Decisions so far

| # | Decision | Notes |
|---|----------|-------|
| 1 | PostgreSQL is the only supported engine in v1 | Keep a connector interface for later |
| 2 | Connect with a **single connection string** | No separate host/user/password fields |
| 3 | Non-read-only role: **warn only**, do not block | Safety is enforced per query regardless (see Safety) |
| 4 | App DB is SQLite | Fewer moving parts than a second Postgres |
| 5 | No raw SQL editor in v1 | Query builder is the only way in |
| 6 | Every query runs in a read-only transaction and is rolled back | Holds even if the database user can write. Tests prove it, and fail if the line is removed |
| 7 | Browsing uses OFFSET paging, fetching one row more than the page to know if there is a next page | No `count(*)`. Browsing stops at row 100,000: sort to reach rows beyond that |
| 8 | Paging orders by the user's sort, then the primary key | Without a total order, OFFSET pages can repeat or skip rows |
| 9 | Row API takes `schema` and `table` as query parameters | Names can contain `/` and `.` |
| 11 | The query model joins tables along declared foreign keys (LEFT JOIN, the key may be on either table, each table at most once) | Users never write ON. A join names a table, the table it comes from, and the key |
| 12 | Grouped questions with no chosen sort are ordered by their grouping columns | Postgres returns groups in arbitrary order, which scrambles any time series |
| 13 | A date typed against a timestamp column means the whole day | "on or before 31 March" includes the evening of the 31st |
| 14 | `query/compile` validates a question and returns its SQL without touching the source DB | The builder shows live SQL as you type. Saving also validates, so a saved question is never broken from the start |
| 15 | Saved questions store the query model and chart choice, never results. Dashboards re-run every card | Always current. As slow as the source database |
| 16 | Charts: bar, line, pie, plus a table view of every result. Palette validated for colour-blind separation. Time axes stay as columns. Pies fold past six slices into "Other" and always have a legend | Bars compare better than pies, but pie was asked for |
| 17 | A dashboard's date range and granularity are applied when a card runs, never saved into the question | Same as Mixpanel boards: opening a question alone still shows what it was saved with. `TimeScope` does it, as a pure function with tests |
| 18 | The date column a range applies to is the one the question already buckets by time, else the source table's most date-like column (`created_at`, `happened_at`...). Tables with none say "no date field" | No per-card setting to configure. A card is honest about what it filtered |
| 19 | Adding a card starts on the dashboard: choose a kind, pick a table, get a pre-filled question that is already run, name suggested | Saving a question elsewhere and then hunting for it on a dashboard was backwards |
| 20 | The question builder is a pop-up over the current page. There are no "New question" or "Saved questions" pages: saved questions are reused and deleted from the Add card dialog. Escape and clicking outside do not close the builder | Nothing is navigated away from, and a half-built question is not thrown away by accident |
| 10 | Only the string the user typed is stored. Our defaults (5s connect timeout) are applied at connect time | Defaults can change without touching saved connections |

## Flows

### A. Connect
1. User pastes one connection string (key/value form, or `postgresql://` URI converted to key/value).
2. Backend parses it with `NpgsqlConnectionStringBuilder`.
3. Open a connection and run `SELECT 1`.
4. Check whether the role can write (`has_table_privilege(..., 'INSERT,UPDATE,DELETE')`, `rolsuper` in `pg_roles`).
   If it can: connect anyway and show a warning banner.
5. Encrypt the string, store it in the App DB, trigger Flow B.
6. After saving, never return the string to the browser. Show a masked version only.

### B. Sync (introspection)
Read `information_schema.columns` plus `pg_catalog` for foreign keys and indexes, and primary key constraints.
Store a metadata snapshot (tables, columns, types, PKs, FKs) in the App DB.
The UI reads the snapshot, not the source DB. A "re-sync" button refreshes it.

### C. Browse
`SELECT ... FROM schema.table ORDER BY ... LIMIT n OFFSET m`, with table and column names validated against the snapshot.

### D. Query
UI selections -> `QueryDefinition` (JSON) -> validate against snapshot -> compile to SQL + parameters -> execute (timeout, row cap) -> `{columns, rows, durationMs}`.

### E. Visualise
Chart = result + type + x/y column choice.
Date + number -> line. Category + number -> bar. Small category + number -> pie.

### F. Save
Store the query JSON and chart config in the App DB. A dashboard is a list of saved query ids plus layout. Results are not stored; they re-run fresh.

## Safety (defence in depth)

1. Read-only DB role (the real guarantee; the user is encouraged to provide one).
2. Every query runs in a read-only transaction (`BEGIN READ ONLY`).
3. Statement timeout and a hard row limit.
4. Values are parameters. Identifiers come from the snapshot whitelist and are quoted.
5. No raw SQL input in v1.

Layer 2 is why warn-only (decision 3) is acceptable: a connection string with write rights still cannot change data through DataLens.

## Metabase mapping

| Metabase | DataLens v1 |
|----------|-------------|
| Driver (`:postgres`) | `PostgresConnector` behind `IDatabaseConnector` |
| Sync (`describe-fields`, `describe-fks`) | `SchemaIntrospectionService` |
| MBQL | `QueryDefinition` |
| `mbql->honeysql` -> SQL | `SqlCompiler` |
| `execute-reducible-query` | `QueryExecutor` |
| Card / Dashboard | `SavedQuery` / `Dashboard` |
| Application database | SQLite |

## Open questions

- Login and per-user content. Everything is one shared workspace on localhost for now.
- Filters are always AND. OR groups would need a nested filter model.
- Summaries across a one-to-many join count the "many" side's rows, as SQL does. Worth a warning in the UI.
- Deployment: one container with a mounted volume for SQLite, and a key from the environment.
- A second database engine (the connector interface exists; nothing else does).
