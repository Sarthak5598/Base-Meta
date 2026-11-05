import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { AddCardDialog, TypeTiles } from './AddCardDialog';
import { api } from './api';
import { ChartOrTable } from './ResultView';
import { loadTime, saveTime, TimeBar, toTimeFilter, type TimeSettings } from './TimeControls';
import type { ChartType, Dashboard, QuestionResult, SavedQuery } from './types';

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

interface CardState {
  query: SavedQuery | null;
  result: QuestionResult | null;
  error: string | null;
  loading: boolean;
}

interface Props {
  /** Bumped when saved content changes elsewhere, so this page reloads. */
  version: number;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  /** Start building a new card of this kind for the dashboard. */
  onAddCard: (type: ChartType, dashboard: Dashboard) => void;
  /** Edit a card's question in the builder. */
  onEditCard: (query: SavedQuery, dashboard: Dashboard) => void;
}

/**
 * A dashboard is a row of time controls above a grid of cards. Add a card, pick what kind, build it,
 * and it lands here. The date range and granularity apply to every card; every card runs live.
 */
export function DashboardsPage({ version, selectedId, onSelect, onAddCard, onEditCard }: Props) {
  const [dashboards, setDashboards] = useState<Dashboard[] | null>(null);
  const [queries, setQueries] = useState<SavedQuery[]>([]);
  const [cards, setCards] = useState<Record<string, CardState>>({});
  const [times, setTimes] = useState<Record<string, TimeSettings>>({});
  const [error, setError] = useState<string | null>(null);

  const [adding, setAdding] = useState(false); // the "Add a card" dialog
  const [creating, setCreating] = useState(false); // the new-dashboard form
  const [newName, setNewName] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [refreshTick, setRefreshTick] = useState(0);

  const open = dashboards?.find((d) => d.id === selectedId) ?? null;
  const settings = open ? (times[open.id] ?? loadTime(open.id)) : null;

  const loadAll = useCallback(async () => {
    try {
      const [d, q] = await Promise.all([api.dashboards(), api.queries()]);
      setDashboards(d);
      setQueries(q);
      return d;
    } catch (e) {
      setError(message(e));
      setDashboards([]);
      return [];
    }
  }, []);

  useEffect(() => {
    void loadAll().then((d) => {
      if (!d.some((x) => x.id === selectedId)) onSelect(d[0]?.id ?? null);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadAll, version]);

  // ---- running the cards ----------------------------------------------------------------------

  // Answers to an older request for the same card are ignored, so a fast change of range never shows stale data.
  const latest = useRef<Record<string, number>>({});
  const timeKey = settings ? JSON.stringify(toTimeFilter(settings)) : '';
  const cardIds = open?.cards.map((c) => c.queryId).join(',') ?? '';

  useEffect(() => {
    if (!open || !settings) return;
    const filter = toTimeFilter(settings);
    for (const card of open.cards) {
      const id = card.queryId;
      const token = (latest.current[id] = (latest.current[id] ?? 0) + 1);
      // keep what is on screen while it reloads: dimmed, no layout jump
      setCards((c) => ({ ...c, [id]: { ...(c[id] ?? { query: null, result: null }), error: null, loading: true } }));
      api
        .runSaved(id, filter)
        .then(({ query, result }) => {
          if (latest.current[id] === token) setCards((c) => ({ ...c, [id]: { query, result, error: null, loading: false } }));
        })
        .catch((e) => {
          if (latest.current[id] === token)
            setCards((c) => ({ ...c, [id]: { query: c[id]?.query ?? null, result: null, error: message(e), loading: false } }));
        });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, cardIds, timeKey, refreshTick]);

  // ---- actions --------------------------------------------------------------------------------

  const changeTime = (next: TimeSettings) => {
    if (!open) return;
    setTimes((t) => ({ ...t, [open.id]: next }));
    saveTime(open.id, next);
  };

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const d = await api.createDashboard(newName.trim());
      setDashboards((all) => [d, ...(all ?? [])]);
      onSelect(d.id);
      setNewName('');
      setCreating(false);
    } catch (err) {
      setError(message(err));
    }
  };

  const replace = (d: Dashboard) => setDashboards((all) => (all ?? []).map((x) => (x.id === d.id ? d : x)));

  const addExisting = async (queryId: string) => {
    if (!open) return;
    setAdding(false);
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
      const d = await loadAll();
      onSelect(d[0]?.id ?? null);
    } catch (e) {
      setError(message(e));
    }
  };

  const addable = useMemo(() => queries.filter((q) => !open?.cards.some((c) => c.queryId === q.id)), [queries, open]);

  // ---- render ---------------------------------------------------------------------------------

  if (dashboards === null) return <div className="center muted">Loading…</div>;

  const newForm = (
    <form className="new-dash" onSubmit={create}>
      <label htmlFor="dash-name" className="field-label">
        Name your dashboard
      </label>
      <div className="row">
        <input id="dash-name" autoFocus value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="Sales overview" maxLength={100} />
        <button type="submit" className="primary" disabled={!newName.trim()}>
          Create
        </button>
        {dashboards.length > 0 && (
          <button type="button" onClick={() => setCreating(false)}>
            Cancel
          </button>
        )}
      </div>
    </form>
  );

  // nothing yet: the first thing to do is make a dashboard
  if (dashboards.length === 0) {
    return (
      <div className="page narrow-page">
        <h1>Dashboards</h1>
        {error && (
          <div className="banner error" role="alert">
            {error}
          </div>
        )}
        <div className="empty-dash">
          <h2>Create your first dashboard</h2>
          <p className="muted">A dashboard is a set of charts and tables for one purpose, with one date range for all of them.</p>
          {newForm}
        </div>
      </div>
    );
  }

  return (
    <div className="page dash-page">
      <div className="dash-top">
        <div className="dash-title-row">
          {creating ? (
            newForm
          ) : (
            <>
              <select
                className="dash-title"
                aria-label="Dashboard"
                value={open?.id ?? ''}
                onChange={(e) => {
                  onSelect(e.target.value);
                  setConfirming(false);
                }}
              >
                {dashboards.map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name}
                  </option>
                ))}
              </select>
              <button type="button" className="link-button small" onClick={() => setCreating(true)}>
                + New dashboard
              </button>
            </>
          )}
        </div>

        {open && (
          <div className="dash-buttons">
            <button type="button" onClick={() => setRefreshTick((t) => t + 1)} disabled={open.cards.length === 0}>
              Refresh
            </button>
            <button type="button" className="primary" onClick={() => setAdding(true)}>
              + Add card
            </button>
            <details className="menu">
              <summary aria-label="More">⋯</summary>
              <div className="menu-items">
                {confirming ? (
                  <>
                    <span className="small">Delete “{open.name}”? Its saved questions stay.</span>
                    <button type="button" className="danger" onClick={deleteDashboard}>
                      Yes, delete
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
            </details>
          </div>
        )}
      </div>

      {open && settings && <TimeBar settings={settings} onChange={changeTime} />}

      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}

      {open && open.cards.length === 0 && (
        <div className="empty-dash">
          <h2>Add your first card</h2>
          <p className="muted">What do you want to see? You can change the date range and time period above once it is here.</p>
          <TypeTiles onPick={(type) => onAddCard(type, open)} />
          {addable.length > 0 && (
            <p className="small">
              Or{' '}
              <button type="button" className="link-button" onClick={() => setAdding(true)}>
                use a question you already saved
              </button>
              .
            </p>
          )}
        </div>
      )}

      {open && open.cards.length > 0 && (
        <div className="card-grid">
          {open.cards.map((card) => {
            const state = cards[card.queryId];
            const q = state?.query ?? queries.find((x) => x.id === card.queryId) ?? null;
            const time = state?.result?.time;
            return (
              <article key={card.queryId} className={`dash-card${state?.loading ? ' loading' : ''}`} aria-busy={state?.loading}>
                <header>
                  <div className="card-heading">
                    <h3>{q?.name ?? 'Question'}</h3>
                    {time &&
                      (time.column ? (
                        <span className="muted small" title="The date column the dashboard's range applies to">
                          by {time.column}
                        </span>
                      ) : (
                        <span className="chip" title="This card's table has no date column, so the dashboard's date range cannot apply to it.">
                          no date field
                        </span>
                      ))}
                  </div>
                  <div className="row">
                    {q && (
                      <button type="button" className="link-button small" onClick={() => onEditCard(q, open)}>
                        Edit
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

      {adding && open && (
        <AddCardDialog
          dashboardName={open.name}
          existing={addable}
          onClose={() => setAdding(false)}
          onPickType={(type) => {
            setAdding(false);
            onAddCard(type, open);
          }}
          onPickExisting={addExisting}
        />
      )}
    </div>
  );
}
