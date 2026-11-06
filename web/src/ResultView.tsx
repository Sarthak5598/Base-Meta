import { BarView, LineView, NumberView, PieView } from './ChartView';
import { axisChoices, fitChart, MAX_CHART_ROWS, toData } from './charts';
import { ResultGrid } from './ResultGrid';
import type { ChartConfig, ChartType, QuestionResult } from './types';

const TYPES: { value: ChartType; label: string }[] = [
  { value: 'table', label: 'Table' },
  { value: 'number', label: 'Number' },
  { value: 'bar', label: 'Bar' },
  { value: 'line', label: 'Line' },
  { value: 'pie', label: 'Pie' },
];

/** A result drawn as its chart, or as a table. Used by the builder and by dashboard cards. */
export function ChartOrTable({ result, chart, height = 320 }: { result: QuestionResult; chart: ChartConfig; height?: number }) {
  const fitted = fitChart(chart, result.columns);
  if (fitted.type === 'table') return <ResultGrid columns={result.columns} rows={result.rows} />;

  // a range with nothing in it is not an error: say so, instead of drawing empty axes
  if (result.rows.length === 0)
    return <p className="empty">{result.time?.rangeApplied ? 'Nothing in this date range.' : 'No rows match.'}</p>;

  if (fitted.type === 'number') return <NumberView columns={result.columns} rows={result.rows} />;

  const data = toData(result.columns, result.rows, fitted);
  const capped = result.rows.length > MAX_CHART_ROWS;
  const title = `${fitted.type} chart of ${fitted.y} by ${fitted.x}`;
  const xKind = result.columns.find((c) => c.name === fitted.x)?.kind;
  const timeAxis = xKind === 'date' || xKind === 'timestamp';

  return (
    <figure className="chart" aria-label={title}>
      {fitted.type === 'bar' && <BarView data={data} height={height} timeAxis={timeAxis} />}
      {fitted.type === 'line' && <LineView data={data} height={height} />}
      {fitted.type === 'pie' && <PieView data={data} height={height} />}
      <figcaption className={`chart-caption muted small${capped ? ' capped' : ''}`}>
        {fitted.y} by {fitted.x}
        {capped && ` · the chart shows the first ${MAX_CHART_ROWS} of ${result.rows.length.toLocaleString()} rows; the table has all of them`}
      </figcaption>
    </figure>
  );
}

interface Props {
  result: QuestionResult;
  chart: ChartConfig;
  onChart: (chart: ChartConfig) => void;
  /** Chart height in pixels; larger in the card detail pop-up. */
  height?: number;
}

/** The result with its controls: pick the form, then which columns go where. */
export function ResultView({ result, chart, onChart, height }: Props) {
  const { xs, ys } = axisChoices(result.columns);
  const canChart = ys.length > 0 && xs.length > 1;
  const canNumber = ys.length > 0;
  const fitted = fitChart(chart, result.columns);

  const pick = (type: ChartType) =>
    onChart(type === 'table' || type === 'number' ? { type } : { ...fitChart({ ...fitted, type }, result.columns), type });

  return (
    <div className="result">
      <div className="result-bar">
        <div className="segmented" role="group" aria-label="Show results as">
          {TYPES.map((t) => (
            <button
              key={t.value}
              type="button"
              className={fitted.type === t.value ? 'on' : undefined}
              aria-pressed={fitted.type === t.value}
              disabled={t.value === 'number' ? !canNumber : t.value !== 'table' && !canChart}
              title={
                t.value === 'number' && !canNumber
                  ? 'A number card needs a number column'
                  : t.value !== 'table' && t.value !== 'number' && !canChart
                    ? 'Charts need a number column and a column to group by'
                    : undefined
              }
              onClick={() => pick(t.value)}
            >
              {t.label}
            </button>
          ))}
        </div>

        {fitted.type !== 'table' && fitted.type !== 'number' && (
          <div className="axes">
            <label className="inline-field">
              <span className="muted small">Across</span>
              <select value={fitted.x ?? ''} onChange={(e) => onChart({ ...fitted, x: e.target.value })}>
                {xs.filter((c) => c.name !== fitted.y).map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="inline-field">
              <span className="muted small">Values</span>
              <select value={fitted.y ?? ''} onChange={(e) => onChart({ ...fitted, y: e.target.value })}>
                {ys.filter((c) => c.name !== fitted.x).map((c) => (
                  <option key={c.name} value={c.name}>
                    {c.name}
                  </option>
                ))}
              </select>
            </label>
          </div>
        )}
      </div>

      <div className="result-body">
        <ChartOrTable result={result} chart={chart} height={height} />
      </div>
    </div>
  );
}
