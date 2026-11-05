# DataLens

A lightweight database explorer and analytics tool, inspired by Metabase. Connect a PostgreSQL
database with a read-only connection string, learn its structure, and (next) build queries and charts
from it. See [`docs/design.md`](docs/design.md) for the design.

## Status

| Step | State |
|------|-------|
| Connect with one connection string, test it, warn if the user can write | done |
| Sync: read tables, columns, keys, foreign keys, indexes into a snapshot | done |
| App database (SQLite) for saved connections (encrypted) and snapshots | done |
| Data explorer, query builder, SQL compiler, executor, charts | next |

## Try it

You need the .NET 8 SDK and Docker.

```bash
docker compose up -d                  # sample "shop" database on localhost:5433
```

```bash
dotnet run --project src/DataLens.Cli -- connect shop \
  "postgresql://datalens_ro:readonly@localhost:5433/shop"
```

```bash
dotnet run --project src/DataLens.Cli -- show shop
```

Other commands: `sync <name>`, `list`, `show <name> --json`. State is kept in `./.datalens/`
(the app database and the local encryption key). Set `DATALENS_KEY` (base64, 32 bytes) to supply your own key.

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

## Layout

```
DataLens.sln
docker-compose.yml          sample source database now; the app joins it at deploy time
db/init/01-seed.sql         sample "shop" data and the read-only role
docs/design.md              architecture and decisions
src/
  DataLens.Core/            models and interfaces. No dependencies. Everything else points here.
  DataLens.Infrastructure/  Postgres connector (Npgsql), SQLite app store, encryption
  DataLens.Cli/             command line front end (used until the API exists)
  DataLens.Api/             ASP.NET Core API (planned)
  web/                      React + TypeScript UI (planned)
tests/
  DataLens.Tests/           xUnit; unit tests plus Postgres integration tests
```

Dependency direction: `Cli` and `Api` depend on `Infrastructure`, which depends on `Core`.
`Core` depends on nothing, so the query model and compiler (next) stay pure and easy to test.

## Deployment plan

One container. The React app is built and copied into the API's `wwwroot`, so ASP.NET Core serves both
the API and the UI. SQLite lives on a mounted volume. `docker compose up` then runs the app next to the
sample database.
