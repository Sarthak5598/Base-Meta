import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import { BrowsePage } from './BrowsePage';
import { BuilderDialog } from './BuilderDialog';
import { BuilderPage } from './BuilderPage';
import { ConnectForm } from './ConnectForm';
import { DashboardsPage } from './DashboardsPage';
import type { ChartType, Connection, Dashboard, SavedQuery, SchemaResponse, TableRef } from './types';

type Page = 'dashboards' | 'browse';

const NAV: { page: Page; label: string }[] = [
  { page: 'dashboards', label: 'Dashboards' },
  { page: 'browse', label: 'Browse data' },
];

/** What the question builder pop-up is open for. */
interface BuilderRequest {
  key: number;
  /** add: a new card for a dashboard. edit: a card's question. standalone: a question from Browse. */
  mode: 'add' | 'edit' | 'standalone';
  dashboard: { id: string; name: string } | null;
  presetType: ChartType | null;
  saved: SavedQuery | null;
  startTable: TableRef | null;
  connectionId: string;
}

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

export function App() {
  const [connections, setConnections] = useState<Connection[] | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [schema, setSchema] = useState<SchemaResponse | null>(null);
  const [adding, setAdding] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [page, setPage] = useState<Page>('dashboards');
  const [dashboardId, setDashboardId] = useState<string | null>(null);
  const [builder, setBuilder] = useState<BuilderRequest | null>(null);
  const [contentVersion, setContentVersion] = useState(0); // bumped when saved content changes

  // "Saved ..." is news for a few seconds, not a permanent banner
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), 8000);
    return () => clearTimeout(timer);
  }, [notice]);

  const tables = schema?.snapshot.metadata.tables ?? [];
  const ready = schema !== null && schema.connection.id === activeId;

  // the one-click sample dashboard only makes sense on the sample shop database
  const canDemo =
    ready &&
    ['orders', 'users', 'order_items', 'products', 'categories', 'order_totals', 'legacy_events'].every((name) =>
      tables.some((t) => t.schema === 'public' && t.name === name),
    );

  // ---- connections and structure --------------------------------------------------------------

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

  const loadSchema = useCallback(async (id: string) => {
    setError(null);
    try {
      setSchema(await api.schema(id));
    } catch (e) {
      setSchema(null);
      setError(message(e));
    }
  }, []);

  useEffect(() => {
    if (activeId) void loadSchema(activeId);
  }, [activeId, loadSchema]);

  const connect = async (name: string, connectionString: string) => {
    const result = await api.connect(name, connectionString);
    await loadConnections();
    setWarnings(result.warnings);
    setAdding(false);
    setPage('dashboards');
    if (result.connection.id === activeId) await loadSchema(result.connection.id);
    else setActiveId(result.connection.id);
  };

  const resync = async () => {
    if (!activeId) return;
    await api.refresh(activeId);
    await loadSchema(activeId);
  };

  // ---- the builder pop-up ---------------------------------------------------------------------

  const open = (request: Omit<BuilderRequest, 'key'>) => {
    setNotice(null);
    // a card belongs to the database its question was built on
    if (request.connectionId !== activeId) setActiveId(request.connectionId);
    setBuilder({ ...request, key: Date.now() });
  };

  /** "+ Add card": a new card of this kind, which lands on the dashboard when saved. */
  const addCard = (type: ChartType, dashboard: Dashboard) => {
    setDashboardId(dashboard.id);
    open({
      mode: 'add',
      dashboard: { id: dashboard.id, name: dashboard.name },
      presetType: type,
      saved: null,
      startTable: null,
      connectionId: activeId!,
    });
  };

  /** "Edit" on a card: its question, in the pop-up, with the dashboard still behind it. */
  const editCard = (query: SavedQuery, dashboard: Dashboard) => {
    setDashboardId(dashboard.id);
    open({
      mode: 'edit',
      dashboard: { id: dashboard.id, name: dashboard.name },
      presetType: null,
      saved: query,
      startTable: null,
      connectionId: query.connectionId,
    });
  };

  /** "Ask a question about this" on the Browse page. */
  const askAbout = (table: TableRef) =>
    open({ mode: 'standalone', dashboard: null, presetType: null, saved: null, startTable: table, connectionId: activeId! });

  const closeBuilder = () => setBuilder(null);

  // ---- render ---------------------------------------------------------------------------------

  if (connections === null) return <div className="center muted">Loading…</div>;

  if (adding) {
    return (
      <div className="center">
        <ConnectForm onConnect={connect} onCancel={connections.length > 0 ? () => setAdding(false) : undefined} />
      </div>
    );
  }

  const active = connections.find((c) => c.id === activeId) ?? null;

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

        <nav className="nav" aria-label="Main">
          {NAV.map((n) => (
            <button
              key={n.page}
              type="button"
              className={`nav-item${page === n.page ? ' selected' : ''}`}
              aria-current={page === n.page ? 'page' : undefined}
              onClick={() => setPage(n.page)}
            >
              {n.label}
            </button>
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

        {notice && (
          <div className="banner ok" role="status">
            {notice}
            <button type="button" className="link-button" onClick={() => setNotice(null)}>
              Dismiss
            </button>
          </div>
        )}

        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}

        {page === 'dashboards' && (
          <DashboardsPage
            version={contentVersion}
            selectedId={dashboardId}
            onSelect={setDashboardId}
            onAddCard={addCard}
            onEditCard={editCard}
            demo={{ connectionId: activeId, available: canDemo }}
          />
        )}

        {page === 'browse' &&
          (ready ? (
            <BrowsePage key={activeId} connectionId={activeId!} tables={tables} onResync={resync} onAsk={askAbout} />
          ) : (
            !error && <div className="center muted">Loading structure…</div>
          ))}
      </main>

      {builder && (
        <BuilderDialog onClose={closeBuilder}>
          {ready && schema!.connection.id === builder.connectionId ? (
            <BuilderPage
              key={builder.key}
              connectionId={builder.connectionId}
              tables={tables}
              saved={builder.saved}
              startTable={builder.startTable}
              dashboard={builder.dashboard ? { ...builder.dashboard, mode: builder.mode === 'edit' ? 'edit' : 'add' } : null}
              presetType={builder.presetType}
              onSaved={() => setContentVersion((v) => v + 1)}
              onDone={(note) => {
                setContentVersion((v) => v + 1);
                if (note) setNotice(note);
                closeBuilder();
              }}
              onCancel={closeBuilder}
            />
          ) : (
            <div className="center muted">Loading structure…</div>
          )}
        </BuilderDialog>
      )}
    </div>
  );
}
