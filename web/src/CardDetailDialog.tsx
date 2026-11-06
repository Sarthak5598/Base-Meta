import { useEffect, useRef, useState } from 'react';
import { api } from './api';
import { TypeIcon } from './AddCardDialog';
import { computeInsights } from './insights';
import { ResultView } from './ResultView';
import { ResultGrid } from './ResultGrid';
import { resolveRange, TimeBar, toTimeFilter, type TimeSettings } from './TimeControls';
import { formatValue } from './charts';
import type { ChartConfig, QuestionResult, SavedQuery } from './types';

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');

interface Props {
  query: SavedQuery;
  /** What the card already showed, so the pop-up opens with data. */
  result: QuestionResult | null;
  /** The dashboard's range: this pop-up starts from it and may change it for itself only. */
  time: TimeSettings;
  onClose: () => void;
  /** The chart or name was saved: the dashboard reloads. */
  onSaved: () => void;
  onEdit: () => void;
  onRemove: () => void;
}

/**
 * A card, opened up: the chart large, the figures behind it, every row, and the controls to change just this chart.
 * Chart changes stay in the pop-up until Save, so nothing on the dashboard moves by accident.
 */
export function CardDetailDialog({ query, result: initial, time: dashboardTime, onClose, onSaved, onEdit, onRemove }: Props) {
  const [result, setResult] = useState<QuestionResult | null>(initial);
  const [time, setTime] = useState(dashboardTime);
  const [chart, setChart] = useState<ChartConfig>(query.chart);
  const [name, setName] = useState(query.name);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [confirming, setConfirming] = useState(false);

  const dirty = name.trim() !== query.name || JSON.stringify(chart) !== JSON.stringify(query.chart);
  const first = useRef(true);

  // run again when this pop-up's own date range changes (the first run is what the card already had)
  const timeKey = JSON.stringify(toTimeFilter(time));
  useEffect(() => {
    if (first.current && initial) {
      first.current = false;
      return;
    }
    first.current = false;
    let current = true;
    setLoading(true);
    setError(null);
    api
      .runSaved(query.id, toTimeFilter(time))
      .then((r) => current && setResult(r.result))
      .catch((e) => current && setError(message(e)))
      .finally(() => current && setLoading(false));
    return () => {
      current = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [timeKey, query.id]);

  // Escape closes, unless there are unsaved changes to protect
  useEffect(() => {
    const before = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !dirty) onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => {
      document.body.style.overflow = before;
      document.removeEventListener('keydown', onKey);
    };
  }, [dirty, onClose]);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      await api.updateQuery(query.id, query.connectionId, name.trim() || query.name, query.query, chart);
      onSaved();
      onClose();
    } catch (e) {
      setError(message(e));
      setSaving(false);
    }
  };

  const insights = result ? computeInsights(result.columns, result.rows, chart) : null;
  const range = resolveRange(time);
  const showRows = result && chart.type !== 'table';

  return (
    <div className="backdrop builder-backdrop" onMouseDown={(e) => e.target === e.currentTarget && !dirty && onClose()}>
      <div className="dialog-xl card-detail" role="dialog" aria-modal="true" aria-label={`${query.name} in detail`}>
        <button type="button" className="icon dialog-close" aria-label="Close" onClick={onClose}>
          ×
        </button>

        <div className="detail-scroll">
          <header className="detail-head">
            <span className="card-icon">
              <TypeIcon type={chart.type} size={20} />
            </span>
            <div>
              <input className="detail-name" aria-label="Chart name" value={name} maxLength={120} onChange={(e) => setName(e.target.value)} />
              <p className="muted small">
                {result?.time?.column
                  ? `${range.name}${result.time.granularityApplied ? ` · by ${time.granularity}` : ''} · date column ${result.time.column}`
                  : 'This question has no date field, so the date range does not apply.'}
              </p>
            </div>
          </header>

          <TimeBar settings={time} onChange={setTime} />

          {error && (
            <div className="banner error" role="alert">
              {error}
            </div>
          )}

          {result && (
            <div className={`detail-main${loading ? ' loading' : ''}`}>
              <section className="detail-chart" aria-label="Chart">
                <ResultView result={result} chart={chart} onChart={setChart} height={380} />
              </section>

              <aside className="detail-side" aria-label="Key figures">
                <h3>Key figures</h3>
                <dl className="stat-list">
                  {insights?.stats.map((s) => (
                    <div key={s.label} className="stat">
                      <dt className="muted small">{s.label}</dt>
                      <dd>
                        <strong>{s.value}</strong>
                        {s.hint && <span className="muted small">{s.hint}</span>}
                      </dd>
                    </div>
                  ))}
                </dl>

                {insights && insights.top.length > 0 && (
                  <>
                    <h3>{insights.topTitle}</h3>
                    <ol className="top-list">
                      {insights.top.map((t) => (
                        <li key={t.name}>
                          <span className="top-name" title={t.name}>
                            {t.name}
                          </span>
                          <span className="top-value">{formatValue(t.value)}</span>
                          {t.share !== null && (
                            <span className="top-bar" aria-hidden="true">
                              <span style={{ width: `${Math.max(2, t.share * 100)}%` }} />
                            </span>
                          )}
                          {t.share !== null && <span className="muted small top-share">{Math.round(t.share * 100)}%</span>}
                        </li>
                      ))}
                    </ol>
                  </>
                )}
              </aside>
            </div>
          )}

          {!result && !error && <div className="muted">Running…</div>}

          {showRows && (
            <section className="detail-rows">
              <h3>
                All {result.rows.length.toLocaleString()} rows{result.limitReached ? ' (limit reached)' : ''}
              </h3>
              <div className="detail-grid">
                <ResultGrid columns={result.columns} rows={result.rows} label="Every row behind the chart" />
              </div>
            </section>
          )}

          {result && (
            <details className="detail-sql">
              <summary>Show SQL · {result.durationMs} ms</summary>
              <pre>{result.sql}</pre>
            </details>
          )}
        </div>

        <footer className="detail-foot">
          <div className="row">
            <button type="button" onClick={onEdit}>
              Edit question
            </button>
            {confirming ? (
              <>
                <span className="small">Remove from this dashboard?</span>
                <button type="button" className="danger" onClick={onRemove}>
                  Yes, remove
                </button>
                <button type="button" onClick={() => setConfirming(false)}>
                  Keep
                </button>
              </>
            ) : (
              <button type="button" className="link-button" onClick={() => setConfirming(true)}>
                Remove from dashboard
              </button>
            )}
          </div>
          <div className="row">
            {dirty && <span className="muted small">Unsaved changes</span>}
            <button type="button" onClick={onClose}>
              {dirty ? 'Discard' : 'Close'}
            </button>
            <button type="button" className="primary" disabled={!dirty || saving || !name.trim()} onClick={save}>
              {saving ? 'Saving…' : 'Save changes'}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}
