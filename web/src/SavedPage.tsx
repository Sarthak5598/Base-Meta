import { useEffect, useState } from 'react';
import { api } from './api';
import { describeQuery } from './query';
import type { Connection, Dashboard, SavedQuery } from './types';

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

interface Props {
  connections: Connection[];
  /** Bumped when a question is saved elsewhere, so this list reloads. */
  version: number;
  onOpen: (query: SavedQuery) => void;
  onChanged: () => void;
}

/** Everything saved so far. Open one to edit it, add it to a dashboard, or delete it. */
export function SavedPage({ connections, version, onOpen, onChanged }: Props) {
  const [queries, setQueries] = useState<SavedQuery[] | null>(null);
  const [dashboards, setDashboards] = useState<Dashboard[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    Promise.all([api.queries(), api.dashboards()])
      .then(([q, d]) => {
        if (!live) return;
        setQueries(q);
        setDashboards(d);
      })
      .catch((e) => live && setError(message(e)));
    return () => {
      live = false;
    };
  }, [version]);

  const connectionName = (id: string) => connections.find((c) => c.id === id)?.name ?? 'unknown database';

  const addTo = async (dashboardId: string, query: SavedQuery) => {
    setError(null);
    try {
      const updated = await api.addCard(dashboardId, query.id);
      setDashboards((all) => all.map((d) => (d.id === updated.id ? updated : d)));
      setNote(`Added “${query.name}” to ${updated.name}.`);
    } catch (e) {
      setError(message(e));
    }
  };

  const remove = async (query: SavedQuery) => {
    setError(null);
    try {
      await api.deleteQuery(query.id);
      setQueries((all) => (all ?? []).filter((q) => q.id !== query.id));
      setConfirming(null);
      setNote(`Deleted “${query.name}”.`);
      onChanged();
    } catch (e) {
      setError(message(e));
    }
  };

  if (queries === null && !error) return <div className="center muted">Loading…</div>;

  return (
    <div className="page">
      <h1>Saved questions</h1>
      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}
      {note && (
        <div className="banner ok" role="status">
          {note}
        </div>
      )}

      {queries?.length === 0 && (
        <div className="placeholder">Nothing saved yet. Build a question, press Run, then name and save it.</div>
      )}

      <ul className="cards-list">
        {queries?.map((q) => (
          <li key={q.id} className="list-card">
            <div className="list-main">
              <button type="button" className="link-button strong" onClick={() => onOpen(q)}>
                {q.name}
              </button>
              <div className="muted small">{describeQuery(q.query)}</div>
              <div className="muted small">
                {connectionName(q.connectionId)} · shown as {q.chart.type} · saved {new Date(q.createdAt).toLocaleDateString()}
              </div>
            </div>
            <div className="list-actions">
              {dashboards.length > 0 && (
                <select
                  aria-label={`Add ${q.name} to a dashboard`}
                  value=""
                  onChange={(e) => e.target.value && addTo(e.target.value, q)}
                >
                  <option value="">Add to dashboard…</option>
                  {dashboards.map((d) => (
                    <option key={d.id} value={d.id} disabled={d.cards.some((c) => c.queryId === q.id)}>
                      {d.name}
                      {d.cards.some((c) => c.queryId === q.id) ? ' (already there)' : ''}
                    </option>
                  ))}
                </select>
              )}
              <button type="button" onClick={() => onOpen(q)}>
                Open
              </button>
              {confirming === q.id ? (
                <>
                  <button type="button" className="danger" onClick={() => remove(q)}>
                    Yes, delete
                  </button>
                  <button type="button" onClick={() => setConfirming(null)}>
                    Keep
                  </button>
                </>
              ) : (
                <button type="button" onClick={() => setConfirming(q.id)}>
                  Delete
                </button>
              )}
            </div>
          </li>
        ))}
      </ul>

      {queries && queries.length > 0 && dashboards.length === 0 && (
        <p className="muted small">Create a dashboard under Dashboards, then add questions to it from here.</p>
      )}
    </div>
  );
}
