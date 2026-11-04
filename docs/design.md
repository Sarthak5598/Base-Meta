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

- Exact shape of `QueryDefinition` (filters, aggregations, group by, joins via FKs).
- Where the encryption key lives (env var for v1?).
- Frontend flow and screens.
