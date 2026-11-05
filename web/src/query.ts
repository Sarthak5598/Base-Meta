import type {
  AggFn,
  ChartType,
  Column,
  ColumnKind,
  ColumnRef,
  DateBucket,
  Filter,
  FilterOp,
  JoinDef,
  QueryDefinition,
  SortDirection,
  Table,
  TableRef,
} from './types';

// ---- tables ------------------------------------------------------------------------------------

export const sameTable = (a: TableRef, b: TableRef) => a.schema === b.schema && a.table === b.table;
export const tableKey = (t: TableRef) => `${t.schema}.${t.table}`;
const ref = (t: Table): TableRef => ({ schema: t.schema, table: t.name });
export const findTable = (tables: Table[], r: TableRef) => tables.find((t) => sameTable(ref(t), r));

// ---- the draft: what the builder edits ---------------------------------------------------------
// Close to the query model, but filter values stay as the text people typed. The server converts them
// to the column's type, so "5" for a number column and "2026-03-31" for a date both just work.

export interface FilterDraft {
  id: number;
  column: ColumnRef;
  op: FilterOp;
  values: string[];
}

export interface AggregationDraft {
  id: number;
  fn: AggFn;
  column: ColumnRef | null;
}

export interface GroupDraft {
  id: number;
  column: ColumnRef;
  bucket: DateBucket | null;
}

export interface Draft {
  source: TableRef | null;
  joins: JoinDef[];
  columns: ColumnRef[]; // empty = all columns
  filters: FilterDraft[];
  aggregations: AggregationDraft[];
  groups: GroupDraft[];
  sort: { column: string; direction: SortDirection } | null;
  limit: number;
}

let nextId = 1;
export const newId = () => nextId++;

export const emptyDraft = (source: TableRef | null = null): Draft => ({
  source,
  joins: [],
  columns: [],
  filters: [],
  aggregations: [],
  groups: [],
  sort: null,
  limit: 1000,
});

// ---- columns available to the question ---------------------------------------------------------

export interface ColumnOption {
  /** What a <select> stores: unique across the tables of the question. */
  id: string;
  ref: ColumnRef;
  table: Table;
  column: Column;
  label: string;
}

export function tablesInDraft(d: Draft, tables: Table[]): Table[] {
  if (!d.source) return [];
  return [d.source, ...d.joins.map((j) => j.table)]
    .map((r) => findTable(tables, r))
    .filter((t): t is Table => t !== undefined);
}

export function columnOptions(d: Draft, tables: Table[]): ColumnOption[] {
  const inQuery = tablesInDraft(d, tables);
  const many = inQuery.length > 1;
  return inQuery.flatMap((t, i) =>
    t.columns.map((c) => ({
      id: `${tableKey(ref(t))}::${c.name}`,
      // the source table's columns need no table reference
      ref: i === 0 ? { name: c.name } : { name: c.name, table: ref(t) },
      table: t,
      column: c,
      label: many ? `${t.name}.${c.name}` : c.name,
    })),
  );
}

export function optionIdOf(d: Draft, r: ColumnRef): string {
  const t = r.table ?? d.source;
  return t ? `${tableKey(t)}::${r.name}` : '';
}

// ---- joins -------------------------------------------------------------------------------------

export interface JoinOption {
  table: TableRef;
  from: TableRef;
  foreignKey: string;
  /** Plain words: "users, through orders.user_id". */
  label: string;
}

/** Tables one foreign key away from any table already in the question. */
export function joinOptions(d: Draft, tables: Table[]): JoinOption[] {
  const inQuery = d.source ? [d.source, ...d.joins.map((j) => j.table)] : [];
  const taken = (t: TableRef) => inQuery.some((q) => sameTable(q, t));
  const options: JoinOption[] = [];

  for (const from of inQuery) {
    const here = findTable(tables, from);
    if (!here) continue;

    // keys on this table that point at another table
    for (const fk of here.foreignKeys) {
      const target = { schema: fk.refSchema, table: fk.refTable };
      if (!taken(target) && findTable(tables, target))
        options.push({
          table: target,
          from,
          foreignKey: fk.name,
          label: `${target.table}, through ${from.table}.${fk.columns.join(', ')}`,
        });
    }

    // keys on other tables that point at this one
    for (const other of tables) {
      for (const fk of other.foreignKeys) {
        if (fk.refSchema === from.schema && fk.refTable === from.table && !taken(ref(other)))
          options.push({
            table: ref(other),
            from,
            foreignKey: fk.name,
            label: `${other.name}, through ${other.name}.${fk.columns.join(', ')}`,
          });
      }
    }
  }
  return options;
}

const refersTo = (r: ColumnRef | null | undefined, gone: TableRef[], source: TableRef | null) => {
  if (!r) return false;
  const t = r.table ?? source;
  return t !== null && gone.some((g) => sameTable(g, t));
};

/** Removing a table also removes tables joined through it, and anything that used their columns. */
export function removeJoin(d: Draft, table: TableRef): Draft {
  const gone: TableRef[] = [table];
  for (let grew = true; grew; ) {
    grew = false;
    for (const j of d.joins)
      if (!gone.some((g) => sameTable(g, j.table)) && gone.some((g) => sameTable(g, j.from))) {
        gone.push(j.table);
        grew = true;
      }
  }

  const uses = (r: ColumnRef | null | undefined) => refersTo(r, gone, d.source);
  return {
    ...d,
    joins: d.joins.filter((j) => !gone.some((g) => sameTable(g, j.table))),
    columns: d.columns.filter((c) => !uses(c)),
    filters: d.filters.filter((f) => !uses(f.column)),
    aggregations: d.aggregations.filter((a) => !uses(a.column)),
    groups: d.groups.filter((g) => !uses(g.column)),
    sort: null, // result names change when a table goes
  };
}

// ---- filters -----------------------------------------------------------------------------------

export interface OpInfo {
  op: FilterOp;
  label: string;
  inputs: 0 | 1 | 2 | 'list';
}

const info = (op: FilterOp, label: string, inputs: OpInfo['inputs']): OpInfo => ({ op, label, inputs });

export function opsFor(kind: ColumnKind): OpInfo[] {
  const nulls = [info('isNull', 'is empty', 0), info('notNull', 'is not empty', 0)];
  switch (kind) {
    case 'number':
      return [
        info('eq', 'equals', 1),
        info('notEq', 'does not equal', 1),
        info('gt', 'is greater than', 1),
        info('gte', 'is at least', 1),
        info('lt', 'is less than', 1),
        info('lte', 'is at most', 1),
        info('between', 'is between', 2),
        info('in', 'is one of', 'list'),
        ...nulls,
      ];
    case 'date':
    case 'timestamp':
      return [
        info('eq', 'is on', 1),
        info('notEq', 'is not on', 1),
        info('gte', 'is on or after', 1),
        info('gt', 'is after', 1),
        info('lte', 'is on or before', 1),
        info('lt', 'is before', 1),
        info('between', 'is between', 2),
        ...nulls,
      ];
    case 'text':
      return [
        info('contains', 'contains', 1),
        info('eq', 'is', 1),
        info('notEq', 'is not', 1),
        info('startsWith', 'starts with', 1),
        info('endsWith', 'ends with', 1),
        info('in', 'is one of', 'list'),
        ...nulls,
      ];
    case 'boolean':
      return [info('eq', 'is', 1), info('notEq', 'is not', 1), ...nulls];
    default:
      return nulls;
  }
}

export const opInfo = (kind: ColumnKind, op: FilterOp) => opsFor(kind).find((o) => o.op === op);

const splitList = (s: string) =>
  s
    .split(',')
    .map((v) => v.trim())
    .filter((v) => v.length > 0);

export function filterIsComplete(f: FilterDraft, kind: ColumnKind): boolean {
  const o = opInfo(kind, f.op);
  if (!o) return false;
  switch (o.inputs) {
    case 0:
      return true;
    case 1:
      return (f.values[0] ?? '').trim() !== '';
    case 2:
      return (f.values[0] ?? '').trim() !== '' && (f.values[1] ?? '').trim() !== '';
    case 'list':
      return splitList(f.values[0] ?? '').length > 0;
  }
}

// ---- summaries ---------------------------------------------------------------------------------

export interface AggInfo {
  fn: AggFn;
  label: string;
  needsColumn: boolean;
  accepts: (k: ColumnKind) => boolean;
}

const any = () => true;

export const AGGREGATIONS: AggInfo[] = [
  { fn: 'count', label: 'Count of rows', needsColumn: false, accepts: any },
  { fn: 'countDistinct', label: 'Count of distinct values', needsColumn: true, accepts: any },
  { fn: 'sum', label: 'Sum of', needsColumn: true, accepts: (k) => k === 'number' },
  { fn: 'avg', label: 'Average of', needsColumn: true, accepts: (k) => k === 'number' },
  { fn: 'min', label: 'Smallest', needsColumn: true, accepts: (k) => k !== 'boolean' && k !== 'other' },
  { fn: 'max', label: 'Largest', needsColumn: true, accepts: (k) => k !== 'boolean' && k !== 'other' },
];

export const aggInfo = (fn: AggFn) => AGGREGATIONS.find((a) => a.fn === fn)!;

export const BUCKETS: { value: DateBucket; label: string }[] = [
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
  { value: 'quarter', label: 'Quarter' },
  { value: 'year', label: 'Year' },
];

export const isDateKind = (k: ColumnKind) => k === 'date' || k === 'timestamp';

// ---- draft <-> query model ---------------------------------------------------------------------

/** Turns the draft into a query. Half-filled filters and summaries are left out until they are complete. */
export function buildQuery(d: Draft, tables: Table[], withSort = true): QueryDefinition | null {
  if (!d.source) return null;
  const options = columnOptions(d, tables);
  const kindOf = (r: ColumnRef) => options.find((o) => o.id === optionIdOf(d, r))?.column.kind;

  const filters: Filter[] = [];
  for (const f of d.filters) {
    const kind = kindOf(f.column);
    if (!kind || !filterIsComplete(f, kind)) continue;
    const o = opInfo(kind, f.op)!;
    filters.push({
      column: f.column,
      op: f.op,
      values: o.inputs === 0 ? [] : o.inputs === 'list' ? splitList(f.values[0]) : f.values.slice(0, o.inputs).map((v) => v.trim()),
    });
  }

  const aggregations = d.aggregations
    .filter((a) => !aggInfo(a.fn).needsColumn || a.column !== null)
    .map((a) => ({ fn: a.fn, column: aggInfo(a.fn).needsColumn ? a.column : null }));

  const groupBy = d.groups.map((g) => ({ column: g.column, bucket: g.bucket }));
  const summarised = aggregations.length > 0 || groupBy.length > 0;

  return {
    source: d.source,
    joins: d.joins,
    columns: summarised ? [] : d.columns,
    filters,
    groupBy,
    aggregations,
    orderBy: withSort && d.sort ? [{ column: d.sort.column, direction: d.sort.direction }] : [],
    limit: d.limit,
  };
}

export const isSummary = (q: QueryDefinition) => (q.aggregations?.length ?? 0) + (q.groupBy?.length ?? 0) > 0;

/** Opens a saved question in the builder. */
export function draftFromQuery(q: QueryDefinition): Draft {
  return {
    source: q.source,
    joins: q.joins ?? [],
    columns: q.columns ?? [],
    filters: (q.filters ?? []).map((f) => ({
      id: newId(),
      column: f.column,
      op: f.op,
      values: f.op === 'in' ? [(f.values ?? []).map(String).join(', ')] : (f.values ?? []).map(String),
    })),
    aggregations: (q.aggregations ?? []).map((a) => ({ id: newId(), fn: a.fn, column: a.column ?? null })),
    groups: (q.groupBy ?? []).map((g) => ({ id: newId(), column: g.column, bucket: g.bucket ?? null })),
    sort: q.orderBy?.[0] ? { column: q.orderBy[0].column, direction: q.orderBy[0].direction } : null,
    limit: q.limit,
  };
}

/** One line describing a question, for lists. */
export function describeQuery(q: QueryDefinition): string {
  const parts = [q.source.table];
  if (q.joins?.length) parts.push(`+ ${q.joins.map((j) => j.table.table).join(', ')}`);
  const what = [
    ...(q.aggregations ?? []).map((a) => (a.column ? `${a.fn} ${a.column.name}` : a.fn)),
    ...(q.groupBy ?? []).map((g) => `by ${g.bucket ? `${g.bucket} of ` : ''}${g.column.name}`),
  ];
  if (what.length) parts.push(what.join(' '));
  if (q.filters?.length) parts.push(`${q.filters.length} filter${q.filters.length === 1 ? '' : 's'}`);
  return parts.join(' · ');
}

// ---- starting points for a card ----------------------------------------------------------------

const DATE_LIKE = /(^|_)(created|updated|ordered|placed|date|time|at|on)($|_)|_at$|_on$|date$/i;
const CATEGORY_LIKE = /(status|type|category|country|state|kind|event|method|plan|region|channel|source)/i;
const UNIQUE_LIKE = /(^|_)(id|email|name|uuid|url)$/i;

const bestDate = (t: Table) => {
  const dates = t.columns.filter((c) => isDateKind(c.kind));
  return dates.find((c) => DATE_LIKE.test(c.name)) ?? dates[0];
};

const bestCategory = (t: Table) => {
  const texts = t.columns.filter((c) => c.kind === 'text');
  return (
    texts.find((c) => CATEGORY_LIKE.test(c.name)) ??
    texts.find((c) => !UNIQUE_LIKE.test(c.name)) ??
    t.columns.find((c) => c.kind === 'boolean')
  );
};

/**
 * What to pre-fill when someone adds a card of this kind: a count over time for a line, a count per
 * category for a bar or pie, nothing for a table. They can change all of it. The note is shown when the
 * table has nothing suitable.
 */
export function starterFor(type: ChartType, table: Table): { draft: Partial<Draft>; note: string | null } {
  if (type === 'table') return { draft: {}, note: null };

  const count = [{ id: newId(), fn: 'count' as const, column: null }];

  if (type === 'line') {
    const date = bestDate(table);
    if (!date)
      return {
        draft: {},
        note: `${table.name} has no date column, so a line chart has no time to follow. Try another table, or a bar chart.`,
      };
    return {
      draft: { aggregations: count, groups: [{ id: newId(), column: { name: date.name }, bucket: 'month' }] },
      note: null,
    };
  }

  const category = bestCategory(table);
  if (!category)
    return {
      draft: {},
      note: `${table.name} has no obvious category column to split by. Choose one under Summarize, or try a table.`,
    };
  return {
    draft: { aggregations: count, groups: [{ id: newId(), column: { name: category.name }, bucket: null }] },
    note: null,
  };
}

const titleCase = (s: string) => {
  const spaced = s.replace(/_/g, ' ').trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
};

/** "Orders per month", "Orders by status", "Total price by category": a name to start from. */
export function suggestName(d: Draft): string {
  if (!d.source) return '';
  const base = titleCase(d.source.table);

  const first = d.aggregations[0];
  const col = first?.column?.name ?? '';
  const metric = !first
    ? base
    : {
        count: base,
        countDistinct: `Distinct ${col}`,
        sum: `Total ${col}`,
        avg: `Average ${col}`,
        min: `Smallest ${col}`,
        max: `Largest ${col}`,
      }[first.fn];

  const dims = d.groups.map((g) => (g.bucket ? `per ${g.bucket}` : `by ${g.column.name}`));
  return [metric, ...dims].join(' ');
}
