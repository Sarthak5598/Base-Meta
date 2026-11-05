import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api } from './api';
import { ConnectForm } from './ConnectForm';
import { DataGrid } from './DataGrid';
import type { Connection, RowsPage, SchemaResponse, SortDirection, Table } from './types';

interface TableRef {
  schema: string;
  name: string;
}

interface ViewState {
  page: number;
  pageSize: number;
  sort: string | null;
  dir: SortDirection;
}

const FIRST_PAGE: ViewState = { page: 1, pageSize: 50, sort: null, dir: 'asc' };
const PAGE_SIZES = [25, 50, 100, 200];

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

export function App() {
  const [connections, setConnections] = useState<Connection[] | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [schema, setSchema] = useState<SchemaResponse | null>(null);
  const [selected, setSelected] = useState<TableRef | null>(null);
  const [view, setView] = useState<ViewState>(FIRST_PAGE);
  const [data, setData] = useState<RowsPage | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);

  // Ignore answers to requests that were overtaken by a newer one (fast clicking between tables).
  const rowsRequest = useRef(0);

  const tables = schema?.snapshot.metadata.tables ?? [];
  const table = useMemo(
    () => tables.find((t) => t.schema === selected?.schema && t.name === selected?.name) ?? null,
    [tables, selected],
  );

  // ---- loading ---------------------------------------------------------------------------------

  const loadConnections = useCallback(async () => {
    try {
      const list = await api.connections();
      setConnections(list);
      return list;
    } catch (e) {
      setError(message(e));
      setConnections([]);
      return [];
    }
  }, []);

  useEffect(() => {
    void loadConnections().then((list) => {
      if (list.length > 0) setActiveId(list[0].id);
      else setAdding(true);
    });
  }, [loadConnections]);

  const loadSchema = useCallback(async (id: string, keep?: TableRef | null) => {
    setError(null);
    try {
      const response = await api.schema(id);
      setSchema(response);
      const list = response.snapshot.metadata.tables;
      const stillThere = keep && list.some((t) => t.schema === keep.schema && t.name === keep.name);
      setSelected(stillThere ? keep : list[0] ? { schema: list[0].schema, name: list[0].name } : null);
    } catch (e) {
      setSchema(null);
      setSelected(null);
      setError(message(e));
    }
  }, []);

  useEffect(() => {
    if (!activeId) return;
    setData(null);
    setView(FIRST_PAGE);
    void loadSchema(activeId);
  }, [activeId, loadSchema]);

  useEffect(() => {
    if (!activeId || !selected) return;
    const request = ++rowsRequest.current;
    setLoading(true);
    setError(null);
    api
      .rows(activeId, selected.schema, selected.name, view.page, view.pageSize, view.sort, view.dir)
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
  }, [activeId, selected, view]);

  // ---- actions ---------------------------------------------------------------------------------

  const selectTable = (schemaName: string, name: string) => {
    setSelected({ schema: schemaName, name });
    setView((v) => ({ ...FIRST_PAGE, pageSize: v.pageSize }));
  };

  const sortBy = (column: string) =>
    setView((v) => {
      // first click ascending, second descending, third clears the sort
      if (v.sort !== column) return { ...v, sort: column, dir: 'asc', page: 1 };
      if (v.dir === 'asc') return { ...v, dir: 'desc', page: 1 };
      return { ...v, sort: null, dir: 'asc', page: 1 };
    });

  const connect = async (name: string, connectionString: string) => {
    const result = await api.connect(name, connectionString);
    const list = await loadConnections();
    setWarnings(result.warnings);
    setAdding(false);
    if (result.connection.id === activeId) await loadSchema(result.connection.id, selected);
    else setActiveId(list.find((c) => c.id === result.connection.id)?.id ?? result.connection.id);
  };

  const resync = async () => {
    if (!activeId) return;
    setError(null);
    try {
      await api.refresh(activeId);
      await loadSchema(activeId, selected);
      setView((v) => ({ ...v })); // reload rows against the new snapshot
    } catch (e) {
      setError(message(e));
    }
  };

  // ---- relationships of the open table ---------------------------------------------------------

  const linksTo = table?.foreignKeys ?? [];
  const linkedFrom = useMemo(
    () =>
      selected
        ? tables.flatMap((t) =>
            t.foreignKeys
              .filter((fk) => fk.refSchema === selected.schema && fk.refTable === selected.name)
              .map((fk) => ({ table: t, fk })),
          )
        : [],
    [tables, selected],
  );

  const active = connections?.find((c) => c.id === activeId) ?? null;
  const bySchema = useMemo(() => {
    const groups = new Map<string, Table[]>();
    for (const t of tables) groups.set(t.schema, [...(groups.get(t.schema) ?? []), t]);
    return [...groups.entries()];
  }, [tables]);

  // ---- render ----------------------------------------------------------------------------------

  if (connections === null) return <div className="center muted">Loading…</div>;

  if (adding) {
    return (
      <div className="center">
        <ConnectForm onConnect={connect} onCancel={connections.length > 0 ? () => setAdding(false) : undefined} />
      </div>
    );
  }

  const first = data ? (data.page - 1) * data.pageSize + 1 : 0;

  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand">DataLens</div>

        <div className="conn">
          <label htmlFor="conn-select" className="label">
            Database
          </label>
          <div className="row">
            <select id="conn-select" value={activeId ?? ''} onChange={(e) => setActiveId(e.target.value)}>
              {connections.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
            <button type="button" onClick={() => setAdding(true)} title="Add a database">
              + Add
            </button>
          </div>
          {active && (
            <div className="conn-info">
              <span className={`chip ${active.canWrite ? 'warn' : 'ok'}`}>
                {active.canWrite ? 'user can write' : 'read-only user'}
              </span>
              <div className="mono small muted" title={active.maskedConnectionString}>
                {active.maskedConnectionString}
              </div>
            </div>
          )}
        </div>

        <nav className="tables" aria-label="Tables">
          {bySchema.map(([schemaName, list]) => (
            <div key={schemaName}>
              <div className="label">{schemaName}</div>
              <ul>
                {list.map((t) => {
                  const isSelected = t.schema === selected?.schema && t.name === selected?.name;
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
      </aside>

      <main className="main">
        {warnings.length > 0 && (
          <div className="banner warn" role="status">
            {warnings.join(' ')}
            <button type="button" className="link-button" onClick={() => setWarnings([])}>
              Dismiss
            </button>
          </div>
        )}

        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}

        {table ? (
          <>
            <header className="table-head">
              <h1>
                <span className="muted">{table.schema}.</span>
                {table.name}
              </h1>
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
                <DataGrid
                  data={data}
                  table={table}
                  sort={view.sort}
                  dir={view.dir}
                  onSort={sortBy}
                  onFollow={selectTable}
                />
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
                <button
                  type="button"
                  disabled={view.page <= 1 || loading}
                  onClick={() => setView((v) => ({ ...v, page: v.page - 1 }))}
                >
                  Previous
                </button>
                <span className="small">Page {view.page}</span>
                <button
                  type="button"
                  disabled={!data?.hasMore || loading}
                  onClick={() => setView((v) => ({ ...v, page: v.page + 1 }))}
                >
                  Next
                </button>
                <button type="button" onClick={resync} title="Read the database structure again">
                  Re-sync structure
                </button>
              </div>
            </footer>
          </>
        ) : (
          !error && <div className="center muted">This database has no tables we can read.</div>
        )}
      </main>
    </div>
  );
}
