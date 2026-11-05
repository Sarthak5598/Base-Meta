// Mirrors what the API sends and accepts. Property names are camelCase, enums are camelCase strings.

export type ColumnKind = 'number' | 'text' | 'boolean' | 'date' | 'timestamp' | 'other';

export interface Column {
  name: string;
  rawType: string;
  kind: ColumnKind;
  nullable: boolean;
  isPrimaryKey: boolean;
}

export interface ForeignKey {
  name: string;
  columns: string[];
  refSchema: string;
  refTable: string;
  refColumns: string[];
}

export interface Table {
  schema: string;
  name: string;
  kind: 'table' | 'view';
  columns: Column[];
  primaryKey: string[];
  foreignKeys: ForeignKey[];
  indexes: { name: string; columns: string[]; isUnique: boolean }[];
}

export interface Connection {
  id: string;
  name: string;
  maskedConnectionString: string;
  canWrite: boolean;
  createdAt: string;
}

export interface SchemaResponse {
  connection: Connection;
  snapshot: { connectionId: string; version: number; syncedAt: string; metadata: { tables: Table[] } };
}

export interface ConnectResponse {
  connection: Connection;
  warnings: string[];
  snapshot: { version: number; tables: number; views: number; columns: number; foreignKeys: number };
}

export interface RowsPage {
  schema: string;
  table: string;
  columns: Column[];
  rows: unknown[][];
  page: number;
  pageSize: number;
  hasMore: boolean;
  durationMs: number;
  snapshotVersion: number;
}

export type SortDirection = 'asc' | 'desc';

// ---- the query model (see docs/design.md) ----------------------------------------------------

export interface TableRef {
  schema: string;
  table: string;
}

export interface ColumnRef {
  name: string;
  /** Omitted or null means the question's source table. */
  table?: TableRef | null;
}

export interface JoinDef {
  table: TableRef;
  from: TableRef;
  foreignKey: string;
}

export type FilterOp =
  | 'eq'
  | 'notEq'
  | 'lt'
  | 'lte'
  | 'gt'
  | 'gte'
  | 'between'
  | 'in'
  | 'contains'
  | 'startsWith'
  | 'endsWith'
  | 'isNull'
  | 'notNull';

export interface Filter {
  column: ColumnRef;
  op: FilterOp;
  values?: unknown[];
}

export type AggFn = 'count' | 'countDistinct' | 'sum' | 'avg' | 'min' | 'max';

export interface Aggregation {
  fn: AggFn;
  column?: ColumnRef | null;
  alias?: string | null;
}

export type DateBucket = 'day' | 'week' | 'month' | 'quarter' | 'year';

export interface Grouping {
  column: ColumnRef;
  bucket?: DateBucket | null;
}

export interface SortSpec {
  column: string;
  direction: SortDirection;
}

export interface QueryDefinition {
  source: TableRef;
  joins?: JoinDef[];
  columns?: ColumnRef[];
  filters?: Filter[];
  groupBy?: Grouping[];
  aggregations?: Aggregation[];
  orderBy?: SortSpec[];
  limit: number;
}

// ---- results, saved content ------------------------------------------------------------------

export interface ResultColumn {
  name: string;
  kind: ColumnKind;
}

export interface CompiledPreview {
  sql: string;
  parameters: Record<string, unknown>;
  columns: ResultColumn[];
}

export interface QuestionResult {
  columns: ResultColumn[];
  rows: unknown[][];
  limitReached: boolean;
  durationMs: number;
  sql: string;
  parameters: Record<string, unknown>;
}

export type ChartType = 'table' | 'bar' | 'line' | 'pie';

export interface ChartConfig {
  type: ChartType;
  x?: string | null;
  y?: string | null;
}

export interface SavedQuery {
  id: string;
  connectionId: string;
  name: string;
  query: QueryDefinition;
  chart: ChartConfig;
  createdAt: string;
}

export interface Dashboard {
  id: string;
  name: string;
  createdAt: string;
  cards: { queryId: string; position: number }[];
}
