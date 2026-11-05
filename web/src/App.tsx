import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import { BrowsePage } from './BrowsePage';
import { BuilderPage } from './BuilderPage';
import { ConnectForm } from './ConnectForm';
import { DashboardsPage } from './DashboardsPage';
import { SavedPage } from './SavedPage';
import type { ChartType, Connection, Dashboard, SavedQuery, SchemaResponse, TableRef } from './types';

type Page = 'browse' | 'build' | 'saved' | 'dashboards';

const NAV: { page: Page; label: string }[] = [
  { page: 'browse', label: 'Browse data' },
  { page: 'build', label: 'New question' },
  { page: 'saved', label: 'Saved questions' },
  { page: 'dashboards', label: 'Dashboards' },
];

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

export function App() {
  const [connections, setConnections] = useState<Connection[] | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [schema, setSchema] = useState<SchemaResponse | null>(null);
  const [adding, setAdding] = useState(false);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);

  const [page, setPage] = useState<Page>('browse');
  const [editing, setEditing] = useState<SavedQuery | null>(null);
  const [startTable, setStartTable] = useState<TableRef | null>(null);
  const [builderKey, setBuilderKey] = useState(0);
  const [contentVersion, setContentVersion] = useState(0); // bumped when saved content changes

  // building a card for a dashboard, or editing one from it
  const [dashboardId, setDashboardId] = useState<string | null>(null);
  const [dashboardCtx, setDashboardCtx] = useState<{ id: string; name: string; mode: 'add' | 'edit' } | null>(null);
  const [presetType, setPresetType] = useState<ChartType | null>(null);

  const tables = schema?.snapshot.metadata.tables ?? [];
  const ready = schema !== null && schema.connection.id === activeId;

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
    if (result.connection.id === activeId) await loadSchema(result.connection.id);
    else setActiveId(result.connection.id);
  };

  const resync = async () => {
    if (!activeId) return;
    await api.refresh(activeId);
    await loadSchema(activeId);
  };

  // ---- navigation -----------------------------------------------------------------------------

  const newQuestion = (table: TableRef | null = null) => {
    setDashboardCtx(null);
    setPresetType(null);
    setEditing(null);
    setStartTable(table);
    setBuilderKey((k) => k + 1);
    setPage('build');
  };

  const openSaved = (query: SavedQuery) => {
    if (query.connectionId !== activeId) setActiveId(query.connectionId);
    setDashboardCtx(null);
    setPresetType(null);
    setEditing(query);
    setStartTable(null);
    setBuilderKey((k) => k + 1);
    setPage('build');
  };

  /** "Add card" on a dashboard: build a new card of this kind, which lands on the dashboard when saved. */
  const addCard = (type: ChartType, dashboard: Dashboard) => {
    setDashboardId(dashboard.id);
    setDashboardCtx({ id: dashboard.id, name: dashboard.name, mode: 'add' });
    setPresetType(type);
    setEditing(null);
    setStartTable(null);
    setBuilderKey((k) => k + 1);
    setPage('build');
  };

  /** "Edit" on a card: open its question, and come back to the dashboard when done. */
  const editCard = (query: SavedQuery, dashboard: Dashboard) => {
    setDashboardId(dashboard.id);
    setDashboardCtx({ id: dashboard.id, name: dashboard.name, mode: 'edit' });
    setPresetType(null);
    setEditing(query);
    setStartTable(null);
    setBuilderKey((k) => k + 1);
    setPage('build');
  };

  const backToDashboard = () => {
    setDashboardCtx(null);
    setPresetType(null);
    setPage('dashboards');
  };

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
              onClick={() => (n.page === 'build' ? newQuestion() : setPage(n.page))}
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

        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}

        {page === 'browse' &&
          (ready ? (
            <BrowsePage key={activeId} connectionId={activeId!} tables={tables} onResync={resync} onAsk={newQuestion} />
          ) : (
            !error && <div className="center muted">Loading structure…</div>
          ))}

        {page === 'build' &&
          (ready ? (
            <BuilderPage
              key={`${activeId}-${builderKey}`}
              connectionId={activeId!}
              tables={tables}
              saved={editing}
              startTable={startTable}
              dashboard={dashboardCtx}
              presetType={presetType}
              onDone={() => {
                setContentVersion((v) => v + 1);
                backToDashboard();
              }}
              onCancel={backToDashboard}
              onSaved={(q) => {
                setEditing(q);
                setContentVersion((v) => v + 1);
              }}
            />
          ) : (
            !error && <div className="center muted">Loading structure…</div>
          ))}

        {page === 'saved' && (
          <SavedPage
            connections={connections}
            version={contentVersion}
            onOpen={openSaved}
            onChanged={() => setContentVersion((v) => v + 1)}
          />
        )}

        {page === 'dashboards' && (
          <DashboardsPage
            version={contentVersion}
            selectedId={dashboardId}
            onSelect={setDashboardId}
            onAddCard={addCard}
            onEditCard={editCard}
          />
        )}
      </main>
    </div>
  );
}
