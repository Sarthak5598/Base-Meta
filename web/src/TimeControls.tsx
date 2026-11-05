import { useEffect, useRef, useState } from 'react';
import type { DateBucket } from './types';

// A dashboard's time controls: a date range and a granularity. They apply to every card when it runs;
// the saved questions themselves are never changed (the same rule Mixpanel's boards follow).

export type RangePreset = '7d' | '30d' | '90d' | '12m' | 'mtd' | 'ytd' | 'all' | 'custom';

export interface TimeSettings {
  preset: RangePreset;
  /** Only used by the custom range. YYYY-MM-DD, or empty for open-ended. */
  from: string;
  to: string;
  granularity: DateBucket;
}

export const DEFAULT_TIME: TimeSettings = { preset: '12m', from: '', to: '', granularity: 'month' };

export const PRESETS: { value: RangePreset; label: string }[] = [
  { value: '7d', label: 'Last 7 days' },
  { value: '30d', label: 'Last 30 days' },
  { value: '90d', label: 'Last 90 days' },
  { value: '12m', label: 'Last 12 months' },
  { value: 'mtd', label: 'This month' },
  { value: 'ytd', label: 'This year' },
  { value: 'all', label: 'All time' },
];

export const GRANULARITIES: { value: DateBucket; label: string }[] = [
  { value: 'day', label: 'Day' },
  { value: 'week', label: 'Week' },
  { value: 'month', label: 'Month' },
  { value: 'quarter', label: 'Quarter' },
  { value: 'year', label: 'Year' },
];

// ---- working out the dates ---------------------------------------------------------------------

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (d: Date) => `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}`;
const utcDay = (y: number, m: number, d: number) => new Date(Date.UTC(y, m, d));

const shortDate = new Intl.DateTimeFormat('en', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
const show = (isoDay: string) => shortDate.format(new Date(`${isoDay}T00:00:00Z`));

export interface ResolvedRange {
  from?: string;
  to?: string;
  /** "Last 12 months" */
  name: string;
  /** "Dec 1, 2025 – Nov 5, 2026" */
  dates: string;
}

/** Dates are UTC days, matching the UTC the server buckets in. "To" is the whole last day. */
export function resolveRange(s: TimeSettings, now: Date = new Date()): ResolvedRange {
  const today = utcDay(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const daysBack = (n: number) => utcDay(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - n);

  let from: Date | undefined;
  let to: Date | undefined = today;
  let name = PRESETS.find((p) => p.value === s.preset)?.label ?? 'Custom range';

  switch (s.preset) {
    case '7d':
      from = daysBack(6);
      break;
    case '30d':
      from = daysBack(29);
      break;
    case '90d':
      from = daysBack(89);
      break;
    case '12m':
      // the first day of the month eleven months ago: exactly twelve monthly points, this month included
      from = utcDay(today.getUTCFullYear(), today.getUTCMonth() - 11, 1);
      break;
    case 'mtd':
      from = utcDay(today.getUTCFullYear(), today.getUTCMonth(), 1);
      break;
    case 'ytd':
      from = utcDay(today.getUTCFullYear(), 0, 1);
      break;
    case 'all':
      to = undefined;
      break;
    case 'custom':
      from = s.from ? new Date(`${s.from}T00:00:00Z`) : undefined;
      to = s.to ? new Date(`${s.to}T00:00:00Z`) : undefined;
      name = 'Custom range';
      break;
  }

  const f = from ? iso(from) : undefined;
  const t = to ? iso(to) : undefined;
  const dates = !f && !t ? 'Every date' : f && t ? `${show(f)} – ${show(t)}` : f ? `From ${show(f)}` : `Up to ${show(t!)}`;
  return { from: f, to: t, name, dates };
}

/** What the API receives when a card runs. */
export function toTimeFilter(s: TimeSettings, now?: Date) {
  const r = resolveRange(s, now);
  return { from: r.from, to: r.to, granularity: s.granularity };
}

// ---- remembering the choice per dashboard (a convenience: the page works without it) ------------

const key = (dashboardId: string) => `datalens.time.v1.${dashboardId}`;

export function loadTime(dashboardId: string): TimeSettings {
  try {
    const raw = localStorage.getItem(key(dashboardId));
    if (!raw) return DEFAULT_TIME;
    const v = JSON.parse(raw) as Partial<TimeSettings>;
    const preset = [...PRESETS.map((p) => p.value), 'custom'].includes(v.preset as string) ? (v.preset as RangePreset) : DEFAULT_TIME.preset;
    const granularity = GRANULARITIES.some((g) => g.value === v.granularity) ? (v.granularity as DateBucket) : DEFAULT_TIME.granularity;
    return { preset, granularity, from: typeof v.from === 'string' ? v.from : '', to: typeof v.to === 'string' ? v.to : '' };
  } catch {
    return DEFAULT_TIME;
  }
}

export function saveTime(dashboardId: string, s: TimeSettings) {
  try {
    localStorage.setItem(key(dashboardId), JSON.stringify(s));
  } catch {
    /* private window or blocked storage: the choice just won't be remembered */
  }
}

// ---- the controls ------------------------------------------------------------------------------

interface Props {
  settings: TimeSettings;
  onChange: (next: TimeSettings) => void;
}

/** One row, above the cards: the date range, then the granularity. */
export function TimeBar({ settings, onChange }: Props) {
  const [open, setOpen] = useState(false);
  const [from, setFrom] = useState(settings.from);
  const [to, setTo] = useState(settings.to);
  const [problem, setProblem] = useState<string | null>(null);
  const root = useRef<HTMLDivElement>(null);
  const range = resolveRange(settings);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!root.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const choose = (preset: RangePreset) => {
    onChange({ ...settings, preset });
    setOpen(false);
  };

  const applyCustom = () => {
    if (!from && !to) return setProblem('Pick a start date, an end date, or both.');
    if (from && to && from > to) return setProblem('The start date is after the end date.');
    setProblem(null);
    onChange({ ...settings, preset: 'custom', from, to });
    setOpen(false);
  };

  return (
    <div className="timebar">
      <div className="range" ref={root}>
        <button
          type="button"
          className="range-button"
          aria-haspopup="listbox"
          aria-expanded={open}
          onClick={() => {
            setFrom(settings.from);
            setTo(settings.to);
            setProblem(null);
            setOpen((o) => !o);
          }}
        >
          <span className="range-name">{range.name}</span>
          <span className="range-dates">{range.dates}</span>
          <span aria-hidden="true">▾</span>
        </button>

        {open && (
          <div className="popover" role="presentation">
            <ul role="listbox" aria-label="Date range">
              {PRESETS.map((p) => (
                <li key={p.value} role="option" aria-selected={settings.preset === p.value}>
                  <button type="button" className="preset-row" onClick={() => choose(p.value)}>
                    <span className="tick" aria-hidden="true">
                      {settings.preset === p.value ? '✓' : ''}
                    </span>
                    {p.label}
                  </button>
                </li>
              ))}
            </ul>
            <div className="popover-foot">
              <div className="foot-title">Custom range</div>
              <div className="row">
                <label className="inline-field">
                  <span className="muted small">From</span>
                  <input type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
                </label>
                <label className="inline-field">
                  <span className="muted small">To</span>
                  <input type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
                </label>
              </div>
              {problem && <div className="small field-error">{problem}</div>}
              <button type="button" onClick={applyCustom}>
                Apply
              </button>
            </div>
          </div>
        )}
      </div>

      <div className="segmented" role="group" aria-label="Group by time period">
        {GRANULARITIES.map((g) => (
          <button
            key={g.value}
            type="button"
            className={settings.granularity === g.value ? 'on' : undefined}
            aria-pressed={settings.granularity === g.value}
            onClick={() => onChange({ ...settings, granularity: g.value })}
          >
            {g.label}
          </button>
        ))}
      </div>
    </div>
  );
}
