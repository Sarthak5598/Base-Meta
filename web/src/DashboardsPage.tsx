import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { CardDetailDialog } from './CardDetailDialog';
import { AddCardDialog, TypeIcon, TypeTiles } from './AddCardDialog';
import { api } from './api';
import { ChartOrTable } from './ResultView';
import { GRANULARITIES, loadTime, resolveRange, saveTime, TimeBar, toTimeFilter, type TimeSettings } from './TimeControls';
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
  /** The one-click sample dashboard, offered when the connected database is the sample shop. */
  demo: { connectionId: string | null; available: boolean };
}

/**
 * How wide a card is on a 12 column grid, from what it shows: a row of headline numbers runs the full width,
 * a single number is a small tile, charts take a third or a half, and a wide table takes the whole row.
 */
function cardSpan(q: SavedQuery | null): number {
  if (!q) return 6;
  switch (q.chart.type) {
    case 'number':
      return (q.query.aggregations?.length ?? 0) > 1 && !q.query.groupBy?.length ? 12 : 3;
    case 'pie':
    case 'bar':
      return 4;
    case 'line':
      return 6;
    case 'table':
      return (q.query.columns?.length ?? 0) >= 5 ? 12 : 6;
  }
}

const WarningIcon = () => (
  <svg viewBox="0 0 24 24" width="22" height="22" aria-hidden="true" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 3 L22 20 L2 20 Z M12 10 V14 M12 17.2 V17.3" />
  </svg>
);

/**
 * A dashboard is a row of time controls above a grid of cards. Add a card, pick what kind, build it,
 * and it lands here. The date range and granularity apply to every card; every card runs live.
 */
export function DashboardsPage({ version, selectedId, onSelect, onAddCard, onEditCard, demo }: Props) {
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
  const [building, setBuilding] = useState(false); // the sample dashboard is being made
  const [detailId, setDetailId] = useState<string | null>(null); // the card opened up
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null); // when a card last finished running
  const [fullscreen, setFullscreen] = useState(false);
  const [everyMin, setEveryMin] = useState(0); // auto-refresh interval in minutes, 0 = off
  const page = useRef<HTMLDivElement>(null);

  // The ⋯ menu closes when something is chosen, when you click elsewhere, and on Escape.
  const menu = useRef<HTMLDetailsElement>(null);
  const closeMenu = () => {
    if (menu.current) menu.current.open = false;
    setConfirming(false);
  };
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (menu.current?.open && !menu.current.contains(e.target as Node)) menu.current.open = false;
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && menu.current?.open) menu.current.open = false;
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, []);

  // fullscreen follows the browser: Escape leaves it, and the button reflects it
  useEffect(() => {
    const onChange = () => setFullscreen(document.fullscreenElement === page.current);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);
  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void page.current?.requestFullscreen?.();
  };

  const open = dashboards?.find((d) => d.id === selectedId) ?? null;

  // the refresh interval is remembered per dashboard; it re-runs every card, so it is off unless chosen
  useEffect(() => {
    try {
      setEveryMin(Number(localStorage.getItem(`datalens.refresh.${selectedId}`) ?? 0) || 0);
    } catch {
      setEveryMin(0);
    }
  }, [selectedId]);
  const chooseEvery = (minutes: number) => {
    setEveryMin(minutes);
    try {
      localStorage.setItem(`datalens.refresh.${selectedId}`, String(minutes));
    } catch {
      /* private window: the choice just is not remembered */
    }
  };
  useEffect(() => {
    if (!everyMin) return;
    const id = window.setInterval(() => {
      if (!document.hidden) setRefreshTick((t) => t + 1); // a hidden tab does not keep querying the database
    }, everyMin * 60_000);
    return () => window.clearInterval(id);
  }, [everyMin]);
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
          if (latest.current[id] === token) {
            setCards((c) => ({ ...c, [id]: { query, result, error: null, loading: false } }));
            setUpdatedAt(new Date());
          }
        })
        .catch((e) => {
          if (latest.current[id] === token)
            setCards((c) => ({ ...c, [id]: { query: c[id]?.query ?? null, result: null, error: message(e), loading: false } }));
        });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedId, cardIds, timeKey, refreshTick, version]);   // version: a card was edited, so run it again

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
      closeMenu();
      const d = await loadAll();
      onSelect(d[0]?.id ?? null);
    } catch (e) {
      setError(message(e));
    }
  };

  const loadDemo = async () => {
    if (!demo.connectionId) return;
    setBuilding(true);
    setError(null);
    try {
      const made = await api.createDemoDashboard(demo.connectionId);
      await loadAll();
      onSelect(made.id);
    } catch (e) {
      setError(message(e));
    } finally {
      setBuilding(false);
    }
  };

  const addable = useMemo(() => queries.filter((q) => !open?.cards.some((c) => c.queryId === q.id)), [queries, open]);

  /** Deleting a saved question also takes its card off every dashboard. */
  const deleteQuery = async (queryId: string) => {
    try {
      await api.deleteQuery(queryId);
      await loadAll();
    } catch (e) {
      setError(message(e));
    }
  };

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
          {demo.available && (
            <>
              <p className="muted small or">or start with something full</p>
              <button type="button" onClick={loadDemo} disabled={building}>
                {building ? 'Building the sample dashboard…' : 'Load the sample shop dashboard'}
              </button>
            </>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className={`page dash-page${fullscreen ? ' is-fullscreen' : ''}`} ref={page}>
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
            {updatedAt && open.cards.length > 0 && (
              <span className="muted small updated" title="When the last card finished running">
                Updated {updatedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
              </span>
            )}
            <select
              className="refresh-every"
              aria-label="Auto-refresh"
              value={everyMin}
              onChange={(e) => chooseEvery(Number(e.target.value))}
              title="Re-run every card on a timer. Each refresh queries your database once per card."
            >
              <option value={0}>Auto-refresh off</option>
              <option value={1}>Every 1 min</option>
              <option value={5}>Every 5 min</option>
              <option value={15}>Every 15 min</option>
              <option value={30}>Every 30 min</option>
              <option value={60}>Every hour</option>
            </select>
            <button type="button" onClick={() => setRefreshTick((t) => t + 1)} disabled={open.cards.length === 0}>
              Refresh
            </button>
            <button type="button" aria-pressed={fullscreen} onClick={toggleFullscreen} disabled={open.cards.length === 0}>
              {fullscreen ? 'Exit full screen' : 'Full screen'}
            </button>
            <button type="button" className="primary" onClick={() => setAdding(true)}>
              + Add card
            </button>
            <details className="menu" ref={menu}>
              <summary aria-label="More">⋯</summary>
              <div className="menu-items">
                {demo.available && !dashboards.some((d) => d.name === 'Shop overview') && (
                  <button
                    type="button"
                    onClick={() => {
                      closeMenu();
                      void loadDemo();
                    }}
                    disabled={building}
                  >
                    {building ? 'Building…' : 'Add the sample shop dashboard'}
                  </button>
                )}
                {confirming ? (
                  <>
                    <span className="small">Delete “{open.name}”? Its saved questions stay.</span>
                    <button type="button" className="danger" onClick={deleteDashboard}>
                      Yes, delete
                    </button>
                    <button type="button" onClick={closeMenu}>
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
            // the freshly loaded list wins, so a rename or a new chart type shows at once
            const q = queries.find((x) => x.id === card.queryId) ?? state?.query ?? null;
            const time = state?.result?.time;
            const type = q?.chart.type ?? 'table';
            const granularity = GRANULARITIES.find((g) => g.value === settings?.granularity)?.label.toLowerCase();

            return (
              <article
                key={card.queryId}
                className={`dash-card type-${type}${state?.loading ? ' loading' : ''}`}
                style={{ ['--span' as string]: cardSpan(q) }}
                data-span={cardSpan(q)}
                aria-busy={state?.loading}
                onClick={(e) => {
                  // anywhere on the card opens it, except its own buttons and a chart's hover
                  if (q && !(e.target as HTMLElement).closest('button, a, input, select')) setDetailId(card.queryId);
                }}
              >
                <header>
                  <div className="card-title">
                    <span className="card-icon">
                      <TypeIcon type={type} size={16} />
                    </span>
                    <div className="card-heading">
                      <h3>
                        {q ? (
                          <button type="button" className="card-open" aria-label={`Open ${q.name} in detail`} onClick={() => setDetailId(card.queryId)}>
                            {q.name}
                          </button>
                        ) : (
                          'Question'
                        )}
                      </h3>
                      {time &&
                        (time.column ? (
                          <span className="muted small" title={`Date column: ${time.column}`}>
                            {settings ? resolveRange(settings).name : ''}
                            {time.granularityApplied && granularity ? ` · by ${granularity}` : ''}
                          </span>
                        ) : (
                          <span className="chip" title="This card's table has no date column, so the dashboard's date range cannot apply to it.">
                            no date field
                          </span>
                        ))}
                    </div>
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
                  {state?.error && (
                    <div className="card-error" role="alert">
                      <WarningIcon />
                      <strong>This card could not load</strong>
                      <span className="muted small">{state.error}</span>
                    </div>
                  )}
                  {state?.result && q && <ChartOrTable result={state.result} chart={q.chart} height={cardSpan(q) >= 6 ? 240 : 210} />}
                  {!state?.result && !state?.error && <div className="muted small">Running…</div>}
                </div>
                {state?.result && type !== 'number' && (
                  <footer className="muted small">
                    {state.result.rows.length.toLocaleString()} rows · {state.result.durationMs} ms
                  </footer>
                )}
              </article>
            );
          })}
        </div>
      )}

      {detailId && open && settings && (() => {
        const q = queries.find((x) => x.id === detailId);
        return q ? (
          <CardDetailDialog
            key={`${q.id}:${q.name}:${JSON.stringify(q.chart)}`}
            query={q}
            result={cards[q.id]?.result ?? null}
            time={settings}
            onClose={() => setDetailId(null)}
            onSaved={() => {
              void loadAll().then(() => setRefreshTick((t) => t + 1));
            }}
            onEdit={() => {
              setDetailId(null);
              onEditCard(q, open);
            }}
            onRemove={() => {
              setDetailId(null);
              void removeCard(q.id);
            }}
          />
        ) : null;
      })()}

      {adding && open && (
        <AddCardDialog
          dashboardName={open.name}
          queries={queries}
          onDashboard={open.cards.map((c) => c.queryId)}
          onClose={() => setAdding(false)}
          onPickType={(type) => {
            setAdding(false);
            onAddCard(type, open);
          }}
          onPickExisting={addExisting}
          onDelete={deleteQuery}
        />
      )}
    </div>
  );
}
