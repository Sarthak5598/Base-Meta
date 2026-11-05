import { formatCell, isNumeric } from './format';
import type { ResultColumn } from './types';

interface Props {
  columns: ResultColumn[];
  rows: unknown[][];
  label?: string;
}

/** A plain table of a result. This is also the table view of every chart. */
export function ResultGrid({ columns, rows, label = 'Results' }: Props) {
  return (
    <div className="grid-wrap" tabIndex={0} aria-label={label}>
      <table className="grid">
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.name} className={isNumeric(c.kind) ? 'num' : undefined}>
                <span className="col-name">{c.name}</span>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row, i) => (
            <tr key={i}>
              {row.map((value, j) => {
                const cell = formatCell(value, columns[j].kind);
                return (
                  <td key={columns[j].name} className={isNumeric(columns[j].kind) ? 'num' : undefined} title={cell.text}>
                    {cell.isNull ? <span className="null">NULL</span> : cell.text}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length === 0 && <p className="empty">No rows match.</p>}
    </div>
  );
}
