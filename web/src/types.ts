// Mirrors what the API returns. Property names are camelCase, enums are lowercase strings.

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
