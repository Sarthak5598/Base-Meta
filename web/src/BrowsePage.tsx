import { useEffect, useMemo, useRef, useState } from 'react';
import { api } from './api';
import { DataGrid } from './DataGrid';
import type { RowsPage, SortDirection, Table, TableRef } from './types';

interface ViewState {
  page: number;
  pageSize: number;
  sort: string | null;
  dir: SortDirection;
}

const FIRST_PAGE: ViewState = { page: 1, pageSize: 50, sort: null, dir: 'asc' };
const PAGE_SIZES = [25, 50, 100, 200];

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

interface Props {
  connectionId: string;
  tables: Table[];
  onResync: () => Promise<void>;
  /** Start a question about a table. */
  onAsk: (table: TableRef) => void;
}

/** Look at the rows of any table, page by page, and see how tables link to each other. */
export function BrowsePage({ connectionId, tables, onResync, onAsk }: Props) {
  const [selected, setSelected] = useState<TableRef | null>(null);
  const [view, setView] = useState<ViewState>(FIRST_PAGE);
  const [data, setData] = useState<RowsPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Ignore answers to requests that were overtaken by a newer one (fast clicking between tables).
  const rowsRequest = useRef(0);

  // open the first table, and keep the open one across a re-sync if it still exists
  useEffect(() => {
    setSelected((current) => {
      if (current && tables.some((t) => t.schema === current.schema && t.name === current.table)) return current;
      return tables[0] ? { schema: tables[0].schema, table: tables[0].name } : null;
    });
  }, [tables]);

  useEffect(() => {
    if (!selected) return;
    const request = ++rowsRequest.current;
    setLoading(true);
    setError(null);
    api
      .rows(connectionId, selected.schema, selected.table, view.page, view.pageSize, view.sort, view.dir)
      .then((page) => {
        if (request === rowsRequest.current) setData(page);
      })
      .catch((e) => {
        if (request !== rowsRequest.current) return;
        setData(null);
        setError(message(e));
      })
      .finally(() => {
        if (request === rowsRequest.current) setLoading(false);
      });
  }, [connectionId, selected, view]);

  const table = useMemo(
    () => tables.find((t) => t.schema === selected?.schema && t.name === selected?.table) ?? null,
    [tables, selected],
  );

  const selectTable = (schema: string, name: string) => {
    setSelected({ schema, table: name });
    setView((v) => ({ ...FIRST_PAGE, pageSize: v.pageSize }));
  };

  const sortBy = (column: string) =>
    setView((v) => {
      // first click ascending, second descending, third clears the sort
      if (v.sort !== column) return { ...v, sort: column, dir: 'asc', page: 1 };
      if (v.dir === 'asc') return { ...v, dir: 'desc', page: 1 };
      return { ...v, sort: null, dir: 'asc', page: 1 };
    });

  const linksTo = table?.foreignKeys ?? [];
  const linkedFrom = useMemo(
    () =>
      selected
        ? tables.flatMap((t) =>
            t.foreignKeys
              .filter((fk) => fk.refSchema === selected.schema && fk.refTable === selected.table)
              .map((fk) => ({ table: t, fk })),
          )
        : [],
    [tables, selected],
  );

  const bySchema = useMemo(() => {
    const groups = new Map<string, Table[]>();
    for (const t of tables) groups.set(t.schema, [...(groups.get(t.schema) ?? []), t]);
    return [...groups.entries()];
  }, [tables]);

  const first = data ? (data.page - 1) * data.pageSize + 1 : 0;

  return (
    <div className="browse">
      <nav className="tables" aria-label="Tables">
        {bySchema.map(([schemaName, list]) => (
          <div key={schemaName}>
            <div className="label">{schemaName}</div>
            <ul>
              {list.map((t) => {
                const isSelected = t.schema === selected?.schema && t.name === selected?.table;
                return (
                  <li key={t.name}>
                    <button
                      type="button"
                      className={`table-item${isSelected ? ' selected' : ''}`}
                      aria-current={isSelected ? 'page' : undefined}
                      onClick={() => selectTable(t.schema, t.name)}
                    >
                      <span>{t.name}</span>
                      {t.kind === 'view' && <span className="chip">view</span>}
                    </button>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </nav>

      <div className="browse-main">
        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}

        {table ? (
          <>
            <header className="table-head">
              <div className="head-row">
                <h1>
                  <span className="muted">{table.schema}.</span>
                  {table.name}
                </h1>
                <button type="button" onClick={() => onAsk({ schema: table.schema, table: table.name })}>
                  Ask a question about this
                </button>
              </div>
              <div className="muted small">
                {table.kind} · {table.columns.length} columns
                {table.primaryKey.length > 0 && <> · primary key ({table.primaryKey.join(', ')})</>}
              </div>

              {(linksTo.length > 0 || linkedFrom.length > 0) && (
                <div className="relations">
                  {linksTo.length > 0 && (
                    <div>
                      <span className="label inline">Links to</span>
                      {linksTo.map((fk) => (
                        <button
                          key={fk.name}
                          type="button"
                          className="chip link"
                          onClick={() => selectTable(fk.refSchema, fk.refTable)}
                        >
                          {fk.columns.join(', ')} → {fk.refTable}.{fk.refColumns.join(', ')}
                        </button>
                      ))}
                    </div>
                  )}
                  {linkedFrom.length > 0 && (
                    <div>
                      <span className="label inline">Linked from</span>
                      {linkedFrom.map(({ table: t, fk }) => (
                        <button
                          key={`${t.name}.${fk.name}`}
                          type="button"
                          className="chip link"
                          onClick={() => selectTable(t.schema, t.name)}
                        >
                          {t.name}.{fk.columns.join(', ')}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </header>

            <div className={`grid-area${loading ? ' loading' : ''}`}>
              {data && data.schema === table.schema && data.table === table.name && (
                <DataGrid data={data} table={table} sort={view.sort} dir={view.dir} onSort={sortBy} onFollow={selectTable} />
              )}
              {!data && loading && <div className="center muted">Loading rows…</div>}
            </div>

            <footer className="pager">
              <div className="muted small" aria-live="polite">
                {data
                  ? data.rows.length > 0
                    ? `Rows ${first.toLocaleString()}–${(first + data.rows.length - 1).toLocaleString()}`
                    : 'No rows'
                  : ''}
                {data && ` · ${data.durationMs} ms · snapshot v${data.snapshotVersion}`}
              </div>
              <div className="pager-controls">
                <label htmlFor="page-size" className="muted small">
                  Rows per page
                </label>
                <select
                  id="page-size"
                  value={view.pageSize}
                  onChange={(e) => setView((v) => ({ ...v, pageSize: Number(e.target.value), page: 1 }))}
                >
                  {PAGE_SIZES.map((n) => (
                    <option key={n} value={n}>
                      {n}
                    </option>
                  ))}
                </select>
                <button type="button" disabled={view.page <= 1 || loading} onClick={() => setView((v) => ({ ...v, page: v.page - 1 }))}>
                  Previous
                </button>
                <span className="small">Page {view.page}</span>
                <button type="button" disabled={!data?.hasMore || loading} onClick={() => setView((v) => ({ ...v, page: v.page + 1 }))}>
                  Next
                </button>
                <button
                  type="button"
                  title="Read the database structure again"
                  onClick={() => onResync().then(() => setView((v) => ({ ...v })), (e) => setError(message(e)))}
                >
                  Re-sync structure
                </button>
              </div>
            </footer>
          </>
        ) : (
          !error && <div className="center muted">This database has no tables we can read.</div>
        )}
      </div>
    </div>
  );
}
