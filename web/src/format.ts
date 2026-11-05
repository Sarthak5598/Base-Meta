import type { ColumnKind } from './types';

/** ISO strings from the API read better as "2026-03-01 10:00:00". Keeps the zone so nothing is misleading. */
function formatTimestamp(iso: string): string {
  const match = /^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(?:\.\d+)?(Z|[+-]\d{2}:\d{2})?$/.exec(iso);
  if (!match) return iso;
  const [, date, time, zone] = match;
  return `${date} ${time}${zone ? (zone === 'Z' ? ' UTC' : ` ${zone}`) : ''}`;
}

export type Cell = { text: string; isNull: boolean };

export function formatCell(value: unknown, kind: ColumnKind): Cell {
  if (value === null || value === undefined) return { text: 'NULL', isNull: true };
  if (typeof value === 'boolean') return { text: String(value), isNull: false };
  if (typeof value === 'number') return { text: String(value), isNull: false };
  if (typeof value === 'string') {
    return { text: kind === 'timestamp' ? formatTimestamp(value) : value, isNull: false };
  }
  return { text: JSON.stringify(value), isNull: false };
}

export const isNumeric = (kind: ColumnKind) => kind === 'number';
