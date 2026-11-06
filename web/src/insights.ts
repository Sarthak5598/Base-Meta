import { categoryLabeller, fitChart, formatKpi } from './charts';
import type { ChartConfig, ResultColumn } from './types';

export interface Stat {
  label: string;
  value: string;
  hint?: string;
}

export interface TopItem {
  name: string;
  value: number;
  /** 0..1 of the total; null when shares make no sense (negative values, zero total). */
  share: number | null;
}

export interface Insights {
  stats: Stat[];
  topTitle: string;
  top: TopItem[];
}

const isTime = (c: ResultColumn | undefined) => c?.kind === 'date' || c?.kind === 'timestamp';
const pct = (n: number) => `${n > 0 ? '+' : ''}${(n * 100).toFixed(Math.abs(n) < 0.1 ? 1 : 0)}%`;

/**
 * What is worth saying about a result, worked out from the rows already on screen: no extra query.
 * Totals, the biggest and smallest, the change from the previous period, and a ranked list.
 */
export function computeInsights(columns: ResultColumn[], rows: unknown[][], chart: ChartConfig): Insights {
  const fitted = fitChart(chart, columns);
  const stats: Stat[] = [];

  if (fitted.type === 'bar' || fitted.type === 'line' || fitted.type === 'pie') {
    const xi = columns.findIndex((c) => c.name === fitted.x);
    const yi = columns.findIndex((c) => c.name === fitted.y);
    if (xi >= 0 && yi >= 0 && rows.length > 0) {
      const label = categoryLabeller(columns[xi].kind, rows.map((r) => r[xi]));
      const items = rows.map((r) => ({ name: label(r[xi]), value: Number(r[yi] ?? 0) }));
      const total = items.reduce((s, d) => s + d.value, 0);
      const byValue = [...items].sort((a, b) => b.value - a.value);
      const time = isTime(columns[xi]);

      stats.push({ label: `Total ${fitted.y}`, value: formatKpi(total) });
      stats.push({ label: time ? 'Average per period' : 'Average per group', value: formatKpi(total / items.length) });
      stats.push({ label: time ? 'Best period' : 'Highest', value: formatKpi(byValue[0].value), hint: byValue[0].name });
      stats.push({ label: time ? 'Weakest period' : 'Lowest', value: formatKpi(byValue[byValue.length - 1].value), hint: byValue[byValue.length - 1].name });

      if (time && items.length >= 2) {
        const last = items[items.length - 1];
        const before = items[items.length - 2];
        stats.push({
          label: 'Latest period',
          value: formatKpi(last.value),
          hint: before.value !== 0 ? `${pct((last.value - before.value) / Math.abs(before.value))} vs ${before.name}. The latest period may be incomplete.` : last.name,
        });
      } else {
        stats.push({ label: 'Groups', value: items.length.toLocaleString() });
      }

      const shares = total > 0 && items.every((d) => d.value >= 0);
      return {
        stats,
        topTitle: time ? `Top periods by ${fitted.y}` : `Top by ${fitted.y}`,
        top: byValue.slice(0, 8).map((d) => ({ ...d, share: shares ? d.value / total : null })),
      };
    }
  }

  if (fitted.type === 'number' && rows.length > 0) {
    columns.forEach((c, i) => {
      if (c.kind === 'number') stats.push({ label: c.name, value: formatKpi(Number(rows[0][i] ?? 0)) });
    });
    return { stats, topTitle: '', top: [] };
  }

  stats.push({ label: 'Rows', value: rows.length.toLocaleString() });
  columns.forEach((c, i) => {
    if (c.kind !== 'number' || stats.length > 3 || rows.length === 0) return;
    const values = rows.map((r) => Number(r[i] ?? 0));
    const sum = values.reduce((s, v) => s + v, 0);
    stats.push({ label: c.name, value: formatKpi(sum), hint: `average ${formatKpi(sum / values.length)} · max ${formatKpi(Math.max(...values))}` });
  });
  return { stats, topTitle: '', top: [] };
}
