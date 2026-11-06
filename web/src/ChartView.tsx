import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  LabelList,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';
import { axisNumber, foldSlices, formatKpi, formatValue, type Datum } from './charts';
import type { ResultColumn } from './types';

// Marks follow the data-viz spec: thin bars (<= 24px) with a 4px rounded data end and a square
// baseline, 2px lines, 8px end dots with a 2px surface ring, hairline recessive grid, one series colour.
// Text is always in text tokens, never in the data colour.

const SERIES = 'var(--series-1)';
const GRID = 'var(--grid)';
const AXIS = 'var(--axis)';
const TICK = { fill: 'var(--muted)', fontSize: 11 };

interface TipProps {
  active?: boolean;
  payload?: ReadonlyArray<{ payload?: Datum }>;
  total?: number;
}

/** One tooltip: the value leads, the name follows, a short line key shows which series. */
function Tip({ active, payload, total }: TipProps) {
  const datum = active ? payload?.[0]?.payload : undefined;
  if (!datum) return null;
  return (
    <div className="tip">
      <div className="tip-value">{formatValue(datum.value)}</div>
      <div className="tip-name">
        <span className="tip-key" style={{ background: SERIES }} aria-hidden="true" />
        {datum.name}
        {total ? <span className="muted"> · {Math.round((datum.value / total) * 100)}%</span> : null}
      </div>
    </div>
  );
}

const longest = (data: Datum[]) => data.reduce((n, d) => Math.max(n, d.name.length), 0);

export function BarView({ data, height, timeAxis = false }: { data: Datum[]; height: number; timeAxis?: boolean }) {
  const max = Math.max(0, ...data.map((d) => d.value));
  const fmt = axisNumber(max);
  // Many or long-named categories read better as rows than as columns, but time always runs left to right.
  const horizontal = timeAxis ? data.length > 24 : data.length > 12 || longest(data) > 12;
  const showValues = data.length <= 15;

  if (horizontal) {
    const labelWidth = Math.min(180, Math.max(72, longest(data) * 7 + 8));
    const h = Math.max(height, data.length * 30 + 40);
    return (
      <ResponsiveContainer width="100%" height={h}>
        <BarChart data={data} layout="vertical" margin={{ top: 8, right: showValues ? 48 : 16, bottom: 8, left: 0 }} barCategoryGap={6}>
          <CartesianGrid horizontal={false} stroke={GRID} strokeWidth={1} />
          <XAxis type="number" tick={TICK} tickLine={false} axisLine={false} tickFormatter={fmt} domain={[0, 'auto']} />
          <YAxis type="category" dataKey="name" width={labelWidth} tick={TICK} tickLine={false} axisLine={{ stroke: AXIS }} interval={0} />
          <Tooltip content={<Tip />} cursor={{ fill: 'var(--hover)' }} isAnimationActive={false} />
          <Bar dataKey="value" fill={SERIES} maxBarSize={24} radius={[0, 4, 4, 0]} isAnimationActive={false}>
            {showValues && (
              <LabelList dataKey="value" position="right" formatter={(v) => formatValue(Number(v))} fill="var(--text-secondary)" fontSize={11} />
            )}
          </Bar>
        </BarChart>
      </ResponsiveContainer>
    );
  }

  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={data} margin={{ top: 20, right: 16, bottom: 8, left: 0 }} barCategoryGap="30%">
        <CartesianGrid vertical={false} stroke={GRID} strokeWidth={1} />
        <XAxis dataKey="name" tick={TICK} tickLine={false} axisLine={{ stroke: AXIS }} interval="preserveStartEnd" minTickGap={10} />
        <YAxis tick={TICK} tickLine={false} axisLine={false} tickFormatter={fmt} domain={[0, 'auto']} width={48} />
        <Tooltip content={<Tip />} cursor={{ fill: 'var(--hover)' }} isAnimationActive={false} />
        <Bar dataKey="value" fill={SERIES} maxBarSize={24} radius={[4, 4, 0, 0]} isAnimationActive={false}>
          {showValues && (
            <LabelList dataKey="value" position="top" formatter={(v) => formatValue(Number(v))} fill="var(--text-secondary)" fontSize={11} />
          )}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}

export function LineView({ data, height }: { data: Datum[]; height: number }) {
  const max = Math.max(0, ...data.map((d) => d.value));
  const min = Math.min(0, ...data.map((d) => d.value));
  const fmt = axisNumber(max);
  const last = data.length - 1;
  const dots = data.length <= 30;

  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={data} margin={{ top: 20, right: 48, bottom: 8, left: 0 }}>
        <CartesianGrid vertical={false} stroke={GRID} strokeWidth={1} />
        <XAxis
          dataKey="name"
          tick={TICK}
          tickLine={false}
          axisLine={{ stroke: AXIS }}
          minTickGap={24}
          padding={{ left: 12, right: 12 }}
        />
        <YAxis tick={TICK} tickLine={false} axisLine={false} tickFormatter={fmt} domain={[min, 'auto']} width={48} />
        <Tooltip content={<Tip />} cursor={{ stroke: AXIS, strokeWidth: 1 }} isAnimationActive={false} />
        <Line
          type="linear"
          dataKey="value"
          stroke={SERIES}
          strokeWidth={2}
          strokeLinecap="round"
          strokeLinejoin="round"
          isAnimationActive={false}
          dot={dots ? { r: 4, fill: SERIES, stroke: 'var(--surface)', strokeWidth: 2 } : false}
          activeDot={{ r: 5, fill: SERIES, stroke: 'var(--surface)', strokeWidth: 2 }}
        >
          {/* the value at the end of the line, and nowhere else */}
          <LabelList
            dataKey="value"
            content={(props) => {
              const { x, y, value, index } = props as { x?: number; y?: number; value?: number; index?: number };
              if (index !== last || x === undefined || y === undefined) return null;
              return (
                <text x={x + 10} y={y + 4} fill="var(--text-secondary)" fontSize={11} textAnchor="start">
                  {formatKpi(Number(value))}
                </text>
              );
            }}
          />
        </Line>
      </LineChart>
    </ResponsiveContainer>
  );
}

/** One headline figure, or several side by side (a row of numbers is "Orders, Revenue, Customers"). */
export function NumberView({ columns, rows }: { columns: ResultColumn[]; rows: unknown[][] }) {
  const row = rows[0] ?? [];
  const tiles = columns.map((c, i) => ({ column: c, value: row[i] })).filter((t) => t.column.kind === 'number');
  if (tiles.length === 0) return <p className="empty">This question has no number to show.</p>;

  return (
    <div className="kpis" style={{ ['--kpis' as string]: Math.min(tiles.length, 4) }}>
      {tiles.map(({ column, value }) => {
        const n = value === null || value === undefined ? null : Number(value);
        return (
          <div className="kpi" key={column.name}>
            <div className="kpi-label">{column.name}</div>
            <div className="kpi-value" title={n === null ? undefined : formatValue(n)}>
              {n === null ? '—' : formatKpi(n)}
            </div>
          </div>
        );
      })}
    </div>
  );
}

const SLICE_COLOURS = [1, 2, 3, 4, 5, 6].map((n) => `var(--series-${n})`);

export function PieView({ data, height }: { data: Datum[]; height: number }) {
  const { slices, folded } = foldSlices(data.filter((d) => d.value > 0));
  const total = slices.reduce((s, d) => s + d.value, 0);
  const colour = (i: number) => (folded && i === slices.length - 1 ? 'var(--other)' : SLICE_COLOURS[i]);

  if (slices.length === 0 || data.some((d) => d.value < 0))
    return <p className="empty">A pie needs positive numbers. Choose a bar chart or the table instead.</p>;

  return (
    <div className="pie">
      <div className="pie-plot">
        {/* the total sits in the hole of the donut */}
        <div className="pie-center" aria-hidden="true">
          <div className="pie-total">{formatKpi(total)}</div>
          <div className="muted small">total</div>
        </div>
        <ResponsiveContainer width="100%" height={height}>
          <PieChart>
            <Tooltip content={<Tip total={total} />} isAnimationActive={false} />
            <Pie
              data={slices}
              dataKey="value"
              nameKey="name"
              innerRadius="58%"
              outerRadius="88%"
              stroke="var(--surface)"
              strokeWidth={2}
              isAnimationActive={false}
              startAngle={90}
              endAngle={-270}
            >
              {slices.map((s, i) => (
                <Cell key={s.name} fill={colour(i)} />
              ))}
            </Pie>
          </PieChart>
        </ResponsiveContainer>
      </div>
      {/* the legend carries names, amounts and shares, so colour is never the only way to tell slices apart */}
      <ul className="pie-legend" aria-label="Slices">
        {slices.map((s, i) => (
          <li key={s.name}>
            <span className="swatch" style={{ background: colour(i) }} aria-hidden="true" />
            <span className="legend-name">{s.name}</span>
            <span className="legend-value">{formatValue(s.value)}</span>
            <span className="legend-share">{Math.round((s.value / total) * 100)}%</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
