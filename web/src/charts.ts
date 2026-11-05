import type { ChartConfig, ColumnKind, ResultColumn } from './types';

// a year by day is 366 points: a line handles that, so the cap sits above it
export const MAX_CHART_ROWS = 400;
export const MAX_PIE_SLICES = 6;

const isTime = (k: ColumnKind) => k === 'date' || k === 'timestamp';

/** Columns that can sit on each axis: anything for X, numbers for Y. */
export function axisChoices(columns: ResultColumn[]) {
  return { xs: columns, ys: columns.filter((c) => c.kind === 'number') };
}

/**
 * A first guess at how to draw a result. Only summaries are drawn by default: a plain list of rows
 * (an `id` column is a number too) is a table, not a chart.
 */
export function suggestChart(columns: ResultColumn[], rowCount: number, summarised: boolean): ChartConfig {
  const y = columns.find((c) => c.kind === 'number');
  const x = columns.find((c) => c !== y && c.kind !== 'number') ?? columns.find((c) => c !== y);
  if (!summarised || !x || !y || rowCount === 0 || rowCount > MAX_CHART_ROWS) return { type: 'table' };
  return { type: isTime(x.kind) ? 'line' : 'bar', x: x.name, y: y.name };
}

/** Keeps the chart choice if it still fits this result, otherwise falls back sensibly. */
export function fitChart(chart: ChartConfig, columns: ResultColumn[]): ChartConfig {
  if (chart.type === 'table') return chart;
  const { xs, ys } = axisChoices(columns);
  const x = xs.find((c) => c.name === chart.x) ?? xs.find((c) => !ys.includes(c)) ?? xs[0];
  const y = ys.find((c) => c.name === chart.y && c !== x) ?? ys.find((c) => c !== x);
  return x && y ? { type: chart.type, x: x.name, y: y.name } : { type: 'table' };
}

// ---- number and label formatting ---------------------------------------------------------------

const full = new Intl.NumberFormat('en', { maximumFractionDigits: 2 });
const compact = new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 });

export const formatValue = (n: number) => full.format(n);

/** Whole numbers in full under 10,000, compact beyond; one style per axis. */
export function axisNumber(max: number) {
  return (n: number) => (max >= 10_000 ? compact.format(n) : full.format(n));
}

const monthYear = new Intl.DateTimeFormat('en', { month: 'short', year: 'numeric', timeZone: 'UTC' });

/** Dates from a "by month" question read as "Mar 2026", others as plain dates. */
export function categoryLabeller(kind: ColumnKind, values: unknown[]): (v: unknown) => string {
  if (!isTime(kind)) return (v) => (v === null || v === undefined ? '(empty)' : String(v));

  const dates = values.map((v) => (typeof v === 'string' ? new Date(v) : null));
  const valid = dates.filter((d): d is Date => d !== null && !Number.isNaN(d.getTime()));
  const midnight = valid.every((d) => d.getUTCHours() + d.getUTCMinutes() + d.getUTCSeconds() === 0);
  const firstOfMonth = midnight && valid.every((d) => d.getUTCDate() === 1);

  return (v) => {
    if (typeof v !== 'string') return '(empty)';
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return v;
    if (firstOfMonth) return monthYear.format(d);
    const date = d.toISOString().slice(0, 10);
    return midnight ? date : `${date} ${d.toISOString().slice(11, 16)}`;
  };
}

export interface Datum {
  name: string;
  value: number;
}

export function toData(columns: ResultColumn[], rows: unknown[][], chart: ChartConfig): Datum[] {
  const xi = columns.findIndex((c) => c.name === chart.x);
  const yi = columns.findIndex((c) => c.name === chart.y);
  if (xi < 0 || yi < 0) return [];
  const label = categoryLabeller(columns[xi].kind, rows.map((r) => r[xi]));
  return rows.slice(0, MAX_CHART_ROWS).map((r) => ({ name: label(r[xi]), value: Number(r[yi] ?? 0) }));
}

/** Biggest slices first; the tail becomes one "Other" so a pie never needs more colours than it has. */
export function foldSlices(data: Datum[]): { slices: Datum[]; folded: boolean } {
  const sorted = [...data].sort((a, b) => b.value - a.value);
  if (sorted.length <= MAX_PIE_SLICES + 1) return { slices: sorted, folded: false };
  const head = sorted.slice(0, MAX_PIE_SLICES);
  const other = sorted.slice(MAX_PIE_SLICES).reduce((sum, d) => sum + d.value, 0);
  return { slices: [...head, { name: 'Other', value: other }], folded: true };
}
