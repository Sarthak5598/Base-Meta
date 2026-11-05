import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { typeTitle } from './AddCardDialog';
import { api } from './api';
import { fitChart, suggestChart } from './charts';
import {
  AGGREGATIONS,
  BUCKETS,
  aggInfo,
  buildQuery,
  columnOptions,
  draftFromQuery,
  emptyDraft,
  filterIsComplete,
  isDateKind,
  isSummary,
  joinOptions,
  newId,
  opInfo,
  opsFor,
  optionIdOf,
  removeJoin,
  starterFor,
  suggestName,
  tableKey,
  tablesInDraft,
  type ColumnOption,
  type Draft,
  type FilterDraft,
} from './query';
import { ResultView } from './ResultView';
import type {
  ChartConfig,
  ChartType,
  CompiledPreview,
  QueryDefinition,
  QuestionResult,
  SavedQuery,
  SortDirection,
  Table,
  TableRef,
} from './types';

interface Props {
  connectionId: string;
  tables: Table[];
  /** A saved question to edit. Without one, the builder starts empty. */
  saved: SavedQuery | null;
  /** A table to start from, e.g. when coming from the table browser. */
  startTable: TableRef | null;
  onSaved: (query: SavedQuery) => void;
  /** Set when this question is being made for, or edited from, a dashboard. */
  dashboard?: { id: string; name: string; mode: 'add' | 'edit' } | null;
  /** The kind of card chosen on the dashboard. Pre-fills the question and the chart. */
  presetType?: ChartType | null;
  /** Called after a dashboard card is added or changed. */
  onDone?: () => void;
  /** Back to the dashboard without saving. */
  onCancel?: () => void;
}

interface Preview extends CompiledPreview {
  query: QueryDefinition;
  /** Result column names without the sort, which is what the sort menu offers. */
  names: string[];
}

const message = (e: unknown) => (e instanceof Error ? e.message : 'Something went wrong.');
const refOf = (t: Table): TableRef => ({ schema: t.schema, table: t.name });

export function BuilderPage({ connectionId, tables, saved, startTable, onSaved, dashboard = null, presetType = null, onDone, onCancel }: Props) {
  const [draft, setDraft] = useState<Draft>(() => {
    if (saved) return draftFromQuery(saved.query);
    const table = startTable ? tables.find((t) => t.schema === startTable.schema && t.name === startTable.table) : undefined;
    return table && presetType ? { ...emptyDraft(startTable), ...starterFor(presetType, table).draft } : emptyDraft(startTable ?? null);
  });
  const [hint, setHint] = useState<string | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [compileError, setCompileError] = useState<string | null>(null);
  const [result, setResult] = useState<QuestionResult | null>(null);
  const [runError, setRunError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [chart, setChart] = useState<ChartConfig>(saved?.chart ?? { type: presetType ?? 'table' });
  const chartTouched = useRef(saved !== null || presetType !== null);
  // after choosing a table for a dashboard card, show the result straight away
  const autoRunNext = useRef(false);
  const [showSql, setShowSql] = useState(false);

  const [name, setName] = useState(saved?.name ?? '');
  const nameTouched = useRef(saved !== null);
  const [addedToDashboard, setAddedToDashboard] = useState(false);
  const [savedId, setSavedId] = useState<string | null>(saved?.id ?? null);
  const [saving, setSaving] = useState(false);
  const [saveNote, setSaveNote] = useState<string | null>(null);

  const options = useMemo(() => columnOptions(draft, tables), [draft, tables]);
  const optionById = useMemo(() => new Map(options.map((o) => [o.id, o])), [options]);
  const inQuery = useMemo(() => tablesInDraft(draft, tables), [draft, tables]);
  const related = useMemo(() => joinOptions(draft, tables), [draft, tables]);
  const summarised = draft.aggregations.length > 0 || draft.groups.length > 0;

  const edit = (patch: Partial<Draft>) => setDraft((d) => ({ ...d, ...patch }));

  // ---- live SQL: compile as the question changes, without touching the database ----------------

  useEffect(() => {
    const base = buildQuery(draft, tables, false);
    if (!base) {
      setPreview(null);
      setCompileError(null);
      return;
    }

    const controller = new AbortController();
    const timer = setTimeout(async () => {
      try {
        const first = await api.compile(connectionId, base, controller.signal);
        const names = first.columns.map((c) => c.name);
        let query = base;
        let compiled = first;
        // Only sort by something that is really in the results (a summary may have dropped it).
        if (draft.sort && names.includes(draft.sort.column)) {
          query = buildQuery(draft, tables, true)!;
          compiled = await api.compile(connectionId, query, controller.signal);
        }
        setPreview({ ...compiled, query, names });
        setCompileError(null);
      } catch (e) {
        if (e instanceof DOMException && e.name === 'AbortError') return;
        setPreview(null);
        setCompileError(message(e));
      }
    }, 250);

    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [draft, tables, connectionId]);

  // ---- running --------------------------------------------------------------------------------

  const run = useCallback(
    async (query: QueryDefinition | undefined = preview?.query) => {
      if (!query) return;
      setRunning(true);
      setRunError(null);
      try {
        const next = await api.run(connectionId, query);
        setResult(next);
        setChart((current) =>
          chartTouched.current ? fitChart(current, next.columns) : suggestChart(next.columns, next.rows.length, isSummary(query)),
        );
      } catch (e) {
        setResult(null);
        setRunError(message(e));
      } finally {
        setRunning(false);
      }
    },
    [connectionId, preview],
  );

  // a saved question opens already run
  const autoRan = useRef(false);
  useEffect(() => {
    if (saved && preview && !autoRan.current) {
      autoRan.current = true;
      void run(preview.query);
    }
  }, [saved, preview, run]);

  useEffect(() => {
    if (autoRunNext.current && preview) {
      autoRunNext.current = false;
      void run(preview.query);
    }
  }, [preview, run]);

  // until the user types a name, keep suggesting one that matches the question
  useEffect(() => {
    if (result && !nameTouched.current) setName(suggestName(draft));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [result]);

  const changeChart = (next: ChartConfig) => {
    chartTouched.current = true;
    setChart(next);
  };

  // ---- saving ---------------------------------------------------------------------------------

  const save = async () => {
    if (!preview) return;
    setSaving(true);
    setSaveNote(null);
    try {
      const fitted = result ? fitChart(chart, result.columns) : chart;
      const done = savedId
        ? await api.updateQuery(savedId, connectionId, name.trim(), preview.query, fitted)
        : await api.saveQuery(connectionId, name.trim(), preview.query, fitted);
      setSavedId(done.id);

      if (dashboard?.mode === 'add' && !addedToDashboard) {
        await api.addCard(dashboard.id, done.id);
        setAddedToDashboard(true);
      }

      setSaveNote(
        dashboard ? 'Saved.' : savedId ? 'Changes saved.' : 'Saved. Find it under Saved questions, or add it to a dashboard there.',
      );
      onSaved(done);
      if (dashboard) onDone?.();
    } catch (e) {
      setSaveNote(message(e));
    } finally {
      setSaving(false);
    }
  };

  // ---- editing helpers ------------------------------------------------------------------------

  const pickTable = (key: string) => {
    const t = tables.find((x) => tableKey(refOf(x)) === key);
    const starter = t && presetType ? starterFor(presetType, t) : null;
    setDraft({ ...emptyDraft(t ? refOf(t) : null), ...(starter?.draft ?? {}) });
    setHint(starter?.note ?? null);
    autoRunNext.current = starter !== null && starter.note === null;
    setResult(null);
    setRunError(null);
  };

  const addFilter = () => {
    const first = options[0];
    if (!first) return;
    const op = opsFor(first.column.kind)[0].op;
    edit({ filters: [...draft.filters, { id: newId(), column: first.ref, op, values: [''] }] });
  };

  const updateFilter = (id: number, patch: Partial<FilterDraft>) =>
    edit({ filters: draft.filters.map((f) => (f.id === id ? { ...f, ...patch } : f)) });

  const changeFilterColumn = (id: number, optionId: string) => {
    const o = optionById.get(optionId);
    if (o) updateFilter(id, { column: o.ref, op: opsFor(o.column.kind)[0].op, values: [''] });
  };

  const addAggregation = () => edit({ aggregations: [...draft.aggregations, { id: newId(), fn: 'count', column: null }] });

  const addGroup = () => {
    const pick = options.find((o) => isDateKind(o.column.kind)) ?? options.find((o) => o.column.kind === 'text') ?? options[0];
    if (!pick) return;
    edit({
      groups: [
        ...draft.groups,
        { id: newId(), column: pick.ref, bucket: isDateKind(pick.column.kind) ? 'month' : null },
      ],
    });
  };

  const columnSet = new Set(draft.columns.length === 0 ? options.map((o) => o.id) : draft.columns.map((c) => optionIdOf(draft, c)));
  const toggleColumn = (o: ColumnOption) => {
    const next = new Set(columnSet);
    if (next.has(o.id)) next.delete(o.id);
    else next.add(o.id);
    if (next.size === 0) return; // keep at least one
    edit({ columns: next.size === options.length ? [] : options.filter((x) => next.has(x.id)).map((x) => x.ref) });
  };

  const sortNames = preview?.names ?? [];
  const sortValue = draft.sort && sortNames.includes(draft.sort.column) ? draft.sort.column : '';

  // ---- render ---------------------------------------------------------------------------------

  return (
    <div className="builder">
      <form className="builder-form" onSubmit={(e) => e.preventDefault()}>
        {dashboard && (
          <button type="button" className="link-button small crumb" onClick={onCancel}>
            ← Back to {dashboard.name}
          </button>
        )}
        <h1>
          {dashboard
            ? dashboard.mode === 'add'
              ? `Add a ${typeTitle(presetType ?? chart.type).toLowerCase()} to ${dashboard.name}`
              : `Edit card on ${dashboard.name}`
            : savedId
              ? 'Edit question'
              : 'New question'}
        </h1>
        {hint && (
          <div className="banner warn" role="status">
            {hint}
          </div>
        )}

        <Section title="Data">
          <label className="field">
            <span className="field-label">Start from</span>
            <select
              aria-label="Table to start from"
              value={draft.source ? tableKey(draft.source) : ''}
              onChange={(e) => pickTable(e.target.value)}
            >
              <option value="" disabled>
                Choose a table…
              </option>
              {tables.map((t) => (
                <option key={tableKey(refOf(t))} value={tableKey(refOf(t))}>
                  {t.schema === 'public' ? t.name : `${t.schema}.${t.name}`}
                  {t.kind === 'view' ? ' (view)' : ''}
                </option>
              ))}
            </select>
          </label>

          {draft.joins.length > 0 && (
            <ul className="chips">
              {draft.joins.map((j) => (
                <li key={tableKey(j.table)} className="chip-removable">
                  <span>
                    + {j.table.table} <span className="muted">via {j.from.table}</span>
                  </span>
                  <button type="button" aria-label={`Remove ${j.table.table}`} onClick={() => setDraft(removeJoin(draft, j.table))}>
                    ×
                  </button>
                </li>
              ))}
            </ul>
          )}

          {draft.source && related.length > 0 && (
            <label className="field">
              <span className="field-label">Add related data</span>
              <select
                aria-label="Add a related table"
                value=""
                onChange={(e) => {
                  const o = related[Number(e.target.value)];
                  if (o) edit({ joins: [...draft.joins, { table: o.table, from: o.from, foreignKey: o.foreignKey }] });
                }}
              >
                <option value="" disabled>
                  Choose a related table…
                </option>
                {related.map((o, i) => (
                  <option key={`${tableKey(o.table)}-${o.foreignKey}`} value={i}>
                    {o.label}
                  </option>
                ))}
              </select>
            </label>
          )}
        </Section>

        {draft.source && (
          <>
            <Section title="Filter" hint="Only rows that match every filter.">
              {draft.filters.map((f) => {
                const o = optionById.get(optionIdOf(draft, f.column));
                if (!o) return null;
                const info = opInfo(o.column.kind, f.op);
                const incomplete = !filterIsComplete(f, o.column.kind);
                return (
                  <div className={`row filter${incomplete ? ' incomplete' : ''}`} key={f.id}>
                    <select aria-label="Filter column" value={o.id} onChange={(e) => changeFilterColumn(f.id, e.target.value)}>
                      {options.map((x) => (
                        <option key={x.id} value={x.id}>
                          {x.label}
                        </option>
                      ))}
                    </select>
                    <select
                      aria-label="Filter condition"
                      value={f.op}
                      onChange={(e) => updateFilter(f.id, { op: e.target.value as FilterDraft['op'], values: [''] })}
                    >
                      {opsFor(o.column.kind).map((x) => (
                        <option key={x.op} value={x.op}>
                          {x.label}
                        </option>
                      ))}
                    </select>
                    {info && info.inputs !== 0 && (
                      <span className="values">
                        <ValueInput option={o} value={f.values[0] ?? ''} list={info.inputs === 'list'} onChange={(v) => updateFilter(f.id, { values: [v, f.values[1] ?? ''] })} />
                        {info.inputs === 2 && (
                          <>
                            <span className="muted small">and</span>
                            <ValueInput option={o} value={f.values[1] ?? ''} onChange={(v) => updateFilter(f.id, { values: [f.values[0] ?? '', v] })} />
                          </>
                        )}
                      </span>
                    )}
                    <button type="button" className="icon" aria-label="Remove filter" onClick={() => edit({ filters: draft.filters.filter((x) => x.id !== f.id) })}>
                      ×
                    </button>
                  </div>
                );
              })}
              <button type="button" className="add" onClick={addFilter}>
                + Add a filter
              </button>
            </Section>

            <Section title="Summarize" hint="Count, add up or average rows, optionally for each group.">
              {draft.aggregations.map((a) => {
                const info = aggInfo(a.fn);
                const choices = options.filter((o) => info.accepts(o.column.kind));
                return (
                  <div className="row" key={a.id}>
                    <select
                      aria-label="Summary"
                      value={a.fn}
                      onChange={(e) => {
                        const fn = e.target.value as typeof a.fn;
                        const next = aggInfo(fn);
                        const keep = a.column && options.find((o) => o.id === optionIdOf(draft, a.column!) && next.accepts(o.column.kind));
                        edit({
                          aggregations: draft.aggregations.map((x) =>
                            x.id === a.id ? { ...x, fn, column: next.needsColumn ? (keep ? a.column : (options.find((o) => next.accepts(o.column.kind))?.ref ?? null)) : null } : x,
                          ),
                        });
                      }}
                    >
                      {AGGREGATIONS.map((x) => (
                        <option key={x.fn} value={x.fn}>
                          {x.label}
                        </option>
                      ))}
                    </select>
                    {info.needsColumn && (
                      <select
                        aria-label="Summary column"
                        value={a.column ? optionIdOf(draft, a.column) : ''}
                        onChange={(e) => {
                          const o = optionById.get(e.target.value);
                          if (o) edit({ aggregations: draft.aggregations.map((x) => (x.id === a.id ? { ...x, column: o.ref } : x)) });
                        }}
                      >
                        <option value="" disabled>
                          Choose a column…
                        </option>
                        {choices.map((o) => (
                          <option key={o.id} value={o.id}>
                            {o.label}
                          </option>
                        ))}
                      </select>
                    )}
                    <button type="button" className="icon" aria-label="Remove summary" onClick={() => edit({ aggregations: draft.aggregations.filter((x) => x.id !== a.id) })}>
                      ×
                    </button>
                  </div>
                );
              })}
              <button type="button" className="add" onClick={addAggregation}>
                + Add a summary
              </button>

              {draft.groups.map((g) => {
                const o = optionById.get(optionIdOf(draft, g.column));
                if (!o) return null;
                return (
                  <div className="row" key={g.id}>
                    <span className="muted small">by</span>
                    <select
                      aria-label="Group by column"
                      value={o.id}
                      onChange={(e) => {
                        const next = optionById.get(e.target.value);
                        if (next)
                          edit({
                            groups: draft.groups.map((x) =>
                              x.id === g.id ? { ...x, column: next.ref, bucket: isDateKind(next.column.kind) ? (x.bucket ?? 'month') : null } : x,
                            ),
                          });
                      }}
                    >
                      {options.map((x) => (
                        <option key={x.id} value={x.id}>
                          {x.label}
                        </option>
                      ))}
                    </select>
                    {isDateKind(o.column.kind) && (
                      <select
                        aria-label="Group dates by"
                        value={g.bucket ?? ''}
                        onChange={(e) =>
                          edit({ groups: draft.groups.map((x) => (x.id === g.id ? { ...x, bucket: (e.target.value || null) as typeof g.bucket } : x)) })
                        }
                      >
                        <option value="">Exact value</option>
                        {BUCKETS.map((b) => (
                          <option key={b.value} value={b.value}>
                            {b.label}
                          </option>
                        ))}
                      </select>
                    )}
                    <button type="button" className="icon" aria-label="Remove grouping" onClick={() => edit({ groups: draft.groups.filter((x) => x.id !== g.id) })}>
                      ×
                    </button>
                  </div>
                );
              })}
              <button type="button" className="add" onClick={addGroup}>
                + Group by
              </button>
            </Section>

            {!summarised && (
              <Section title="Columns">
                <details>
                  <summary>
                    {columnSet.size === options.length ? `All ${options.length} columns` : `${columnSet.size} of ${options.length} columns`}
                  </summary>
                  <div className="checks">
                    {inQuery.length > 1 && <p className="muted small">Columns of every table in the question.</p>}
                    {options.map((o) => (
                      <label key={o.id} className="check">
                        <input type="checkbox" checked={columnSet.has(o.id)} onChange={() => toggleColumn(o)} />
                        <span>{o.label}</span>
                        <span className="muted small mono">{o.column.rawType}</span>
                      </label>
                    ))}
                  </div>
                </details>
              </Section>
            )}

            <Section title="Sort and limit">
              <div className="row">
                <select
                  aria-label="Sort by"
                  value={sortValue}
                  onChange={(e) => edit({ sort: e.target.value ? { column: e.target.value, direction: draft.sort?.direction ?? 'asc' } : null })}
                >
                  <option value="">Default order</option>
                  {sortNames.map((n) => (
                    <option key={n} value={n}>
                      {n}
                    </option>
                  ))}
                </select>
                <select
                  aria-label="Sort direction"
                  value={draft.sort?.direction ?? 'asc'}
                  disabled={!sortValue}
                  onChange={(e) => draft.sort && edit({ sort: { ...draft.sort, direction: e.target.value as SortDirection } })}
                >
                  <option value="asc">Ascending</option>
                  <option value="desc">Descending</option>
                </select>
              </div>
              <label className="row">
                <span className="muted small">Show at most</span>
                <input
                  aria-label="Row limit"
                  className="narrow"
                  type="number"
                  min={1}
                  max={10000}
                  value={draft.limit}
                  onChange={(e) => edit({ limit: Math.max(1, Math.min(10000, Math.floor(Number(e.target.value) || 1))) })}
                />
                <span className="muted small">rows</span>
              </label>
            </Section>
          </>
        )}
      </form>

      <div className="builder-main">
        <div className="builder-actions">
          <button type="button" className="primary" onClick={() => run()} disabled={!preview || running}>
            {running ? 'Running…' : 'Run'}
          </button>
          <button type="button" onClick={() => setShowSql((s) => !s)} disabled={!preview} aria-expanded={showSql}>
            {showSql ? 'Hide SQL' : 'Show SQL'}
          </button>
          {result && (
            <span className="muted small" aria-live="polite">
              {result.rows.length.toLocaleString()} row{result.rows.length === 1 ? '' : 's'} · {result.durationMs} ms
              {result.limitReached && ` · limit of ${preview?.query.limit.toLocaleString()} reached`}
            </span>
          )}
        </div>

        {compileError && draft.source && (
          <div className="banner error" role="alert">
            {compileError}
          </div>
        )}
        {runError && (
          <div className="banner error" role="alert">
            {runError}
          </div>
        )}

        {showSql && preview && (
          <div className="sql-panel">
            <pre className="sql">{preview.sql}</pre>
            {Object.keys(preview.parameters).length > 0 && (
              <dl className="params">
                {Object.entries(preview.parameters).map(([k, v]) => (
                  <div key={k}>
                    <dt className="mono">@{k}</dt>
                    <dd className="mono">{Array.isArray(v) ? JSON.stringify(v) : String(v)}</dd>
                  </div>
                ))}
              </dl>
            )}
            <p className="muted small">Values are sent as parameters, never pasted into the SQL.</p>
          </div>
        )}

        {result ? (
          <ResultView result={result} chart={chart} onChart={changeChart} />
        ) : (
          !runError && (
            <div className="placeholder">
              {draft.source
                ? 'Add filters or a summary if you like, then press Run.'
                : 'Choose a table to start a question.'}
            </div>
          )
        )}

        {result && (
          <div className="save">
            <label className="field grow">
              <span className="field-label">Name this question</span>
              <input
                value={name}
                onChange={(e) => {
                  nameTouched.current = true;
                  setName(e.target.value);
                }}
                placeholder="Orders per month"
                maxLength={100}
              />
            </label>
            <button type="button" className="primary" onClick={save} disabled={saving || !name.trim() || !preview}>
              {saving ? 'Saving…' : dashboard?.mode === 'add' ? 'Add to dashboard' : savedId ? 'Save changes' : 'Save question'}
            </button>
            {saveNote && (
              <p className="muted small save-note" role="status">
                {saveNote}
              </p>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <fieldset className="section">
      <legend>{title}</legend>
      {hint && <p className="muted small hint">{hint}</p>}
      {children}
    </fieldset>
  );
}

function ValueInput({
  option,
  value,
  onChange,
  list = false,
}: {
  option: ColumnOption;
  value: string;
  onChange: (v: string) => void;
  list?: boolean;
}) {
  const kind = option.column.kind;
  if (kind === 'boolean')
    return (
      <select aria-label="Value" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="" disabled>
          Choose…
        </option>
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  return (
    <input
      aria-label="Value"
      type={isDateKind(kind) ? 'date' : 'text'}
      inputMode={kind === 'number' ? 'decimal' : undefined}
      value={value}
      placeholder={list ? 'a, b, c' : kind === 'number' ? '0' : ''}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

