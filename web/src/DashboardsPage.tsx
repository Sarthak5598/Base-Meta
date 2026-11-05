import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from './api';
import { ChartOrTable } from './ResultView';
import type { Dashboard, QuestionResult, SavedQuery } from './types';

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

interface CardState {
  query: SavedQuery | null;
  result: QuestionResult | null;
  error: string | null;
  loading: boolean;
}

interface Props {
  version: number;
  onOpen: (query: SavedQuery) => void;
}

/** Saved questions side by side. Every card runs its question live when the dashboard opens. */
export function DashboardsPage({ version, onOpen }: Props) {
  const [dashboards, setDashboards] = useState<Dashboard[] | null>(null);
  const [queries, setQueries] = useState<SavedQuery[]>([]);
  const [openId, setOpenId] = useState<string | null>(null);
  const [cards, setCards] = useState<Record<string, CardState>>({});
  const [newName, setNewName] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);

  const open = dashboards?.find((d) => d.id === openId) ?? null;

  const loadAll = useCallback(async () => {
    try {
      const [d, q] = await Promise.all([api.dashboards(), api.queries()]);
      setDashboards(d);
      setQueries(q);
      setOpenId((current) => (current && d.some((x) => x.id === current) ? current : (d[0]?.id ?? null)));
    } catch (e) {
      setError(message(e));
      setDashboards([]);
    }
  }, []);

  useEffect(() => {
    void loadAll();
  }, [loadAll, version]);

  const runCard = useCallback(async (queryId: string) => {
    setCards((c) => ({ ...c, [queryId]: { ...(c[queryId] ?? { query: null, result: null }), error: null, loading: true } }));
    try {
      const { query, result } = await api.runSaved(queryId);
      setCards((c) => ({ ...c, [queryId]: { query, result, error: null, loading: false } }));
    } catch (e) {
      setCards((c) => ({ ...c, [queryId]: { query: c[queryId]?.query ?? null, result: null, error: message(e), loading: false } }));
    }
  }, []);

  // opening a dashboard runs every card on it, live
  const cardIds = open?.cards.map((c) => c.queryId).join(',') ?? '';
  useEffect(() => {
    if (!open) return;
    for (const card of open.cards) if (!cards[card.queryId]) void runCard(card.queryId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openId, cardIds]);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const d = await api.createDashboard(newName.trim());
      setDashboards((all) => [d, ...(all ?? [])]);
      setOpenId(d.id);
      setNewName('');
    } catch (err) {
      setError(message(err));
    }
  };

  const replace = (d: Dashboard) => setDashboards((all) => (all ?? []).map((x) => (x.id === d.id ? d : x)));

  const add = async (queryId: string) => {
    if (!open || !queryId) return;
    setError(null);
    try {
      replace(await api.addCard(open.id, queryId));
    } catch (e) {
      setError(message(e));
    }
  };

  const removeCard = async (queryId: string) => {
    if (!open) return;
    try {
      replace(await api.removeCard(open.id, queryId));
    } catch (e) {
      setError(message(e));
    }
  };

  const deleteDashboard = async () => {
    if (!open) return;
    try {
      await api.deleteDashboard(open.id);
      setConfirming(false);
      await loadAll();
    } catch (e) {
      setError(message(e));
    }
  };

  const refresh = () => open?.cards.forEach((c) => void runCard(c.queryId));

  if (dashboards === null) return <div className="center muted">Loading…</div>;

  const addable = queries.filter((q) => !open?.cards.some((c) => c.queryId === q.id));

  return (
    <div className="dash">
      <aside className="dash-list">
        <h1>Dashboards</h1>
        <ul>
          {dashboards.map((d) => (
            <li key={d.id}>
              <button
                type="button"
                className={`table-item${d.id === openId ? ' selected' : ''}`}
                aria-current={d.id === openId ? 'page' : undefined}
                onClick={() => {
                  setOpenId(d.id);
                  setConfirming(false);
                }}
              >
                <span>{d.name}</span>
                <span className="chip">{d.cards.length}</span>
              </button>
            </li>
          ))}
        </ul>
        <form className="new-dash" onSubmit={create}>
          <label htmlFor="dash-name" className="field-label">
            New dashboard
          </label>
          <div className="row">
            <input id="dash-name" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Sales overview" maxLength={100} />
            <button type="submit" disabled={!newName.trim()}>
              Create
            </button>
          </div>
        </form>
      </aside>

      <section className="dash-main">
        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}

        {!open ? (
          <div className="placeholder">Create a dashboard, then add saved questions to it.</div>
        ) : (
          <>
            <div className="dash-head">
              <h2>{open.name}</h2>
              <div className="row">
                {addable.length > 0 && (
                  <select aria-label="Add a question" value="" onChange={(e) => add(e.target.value)}>
                    <option value="">Add a question…</option>
                    {addable.map((q) => (
                      <option key={q.id} value={q.id}>
                        {q.name}
                      </option>
                    ))}
                  </select>
                )}
                <button type="button" onClick={refresh} disabled={open.cards.length === 0}>
                  Refresh
                </button>
                {confirming ? (
                  <>
                    <button type="button" className="danger" onClick={deleteDashboard}>
                      Yes, delete dashboard
                    </button>
                    <button type="button" onClick={() => setConfirming(false)}>
                      Keep
                    </button>
                  </>
                ) : (
                  <button type="button" onClick={() => setConfirming(true)}>
                    Delete dashboard
                  </button>
                )}
              </div>
            </div>

            {open.cards.length === 0 ? (
              <div className="placeholder">
                {queries.length === 0
                  ? 'No saved questions yet. Build one, save it, and it can be added here.'
                  : 'Empty. Use “Add a question” above.'}
              </div>
            ) : (
              <div className="card-grid">
                {open.cards.map((card) => {
                  const state = cards[card.queryId];
                  const q = state?.query ?? queries.find((x) => x.id === card.queryId) ?? null;
                  return (
                    <article key={card.queryId} className={`dash-card${state?.loading ? ' loading' : ''}`}>
                      <header>
                        <h3>{q?.name ?? 'Question'}</h3>
                        <div className="row">
                          {q && (
                            <button type="button" className="link-button small" onClick={() => onOpen(q)}>
                              Open
                            </button>
                          )}
                          <button type="button" className="icon" aria-label={`Remove ${q?.name ?? 'question'} from dashboard`} onClick={() => removeCard(card.queryId)}>
                            ×
                          </button>
                        </div>
                      </header>
                      <div className="dash-card-body">
                        {state?.error && <div className="banner error">{state.error}</div>}
                        {state?.result && q && <ChartOrTable result={state.result} chart={q.chart} height={220} />}
                        {!state?.result && !state?.error && <div className="muted small">Running…</div>}
                      </div>
                      {state?.result && (
                        <footer className="muted small">
                          {state.result.rows.length.toLocaleString()} rows · {state.result.durationMs} ms
                        </footer>
                      )}
                    </article>
                  );
                })}
              </div>
            )}
          </>
        )}
      </section>
    </div>
  );
}
