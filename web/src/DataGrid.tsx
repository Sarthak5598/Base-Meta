import { formatCell, isNumeric } from './format';
import type { RowsPage, SortDirection, Table } from './types';

interface Props {
  data: RowsPage;
  table: Table;
  sort: string | null;
  dir: SortDirection;
  onSort: (column: string) => void;
  onFollow: (schema: string, table: string) => void;
}

export function DataGrid({ data, table, sort, dir, onSort, onFollow }: Props) {
  return (
    <div className="grid-wrap" tabIndex={0} aria-label={`Rows of ${table.name}`}>
      <table className="grid">
        <thead>
          <tr>
            {data.columns.map((column) => {
              const fk = table.foreignKeys.find((f) => f.columns.includes(column.name));
              const active = sort === column.name;
              return (
                <th
                  key={column.name}
                  className={isNumeric(column.kind) ? 'num' : undefined}
                  aria-sort={active ? (dir === 'asc' ? 'ascending' : 'descending') : 'none'}
                >
                  <button type="button" className="th-button" onClick={() => onSort(column.name)}>
                    <span className="col-name">{column.name}</span>
                    <span className="sort-mark" aria-hidden="true">
                      {active ? (dir === 'asc' ? '▲' : '▼') : ''}
                    </span>
                  </button>
                  <div className="col-meta">
                    <span className="mono">{column.rawType}</span>
                    {column.isPrimaryKey && <span className="chip">PK</span>}
                    {fk && (
                      <button
                        type="button"
                        className="chip link"
                        title={`Open ${fk.refSchema}.${fk.refTable}`}
                        onClick={() => onFollow(fk.refSchema, fk.refTable)}
                      >
                        → {fk.refTable}
                      </button>
                    )}
                  </div>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {data.rows.map((row, i) => (
            <tr key={i}>
              {row.map((value, j) => {
                const column = data.columns[j];
                const cell = formatCell(value, column.kind);
                return (
                  <td key={column.name} className={isNumeric(column.kind) ? 'num' : undefined} title={cell.text}>
                    {cell.isNull ? <span className="null">NULL</span> : cell.text}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      {data.rows.length === 0 && <p className="empty">No rows on this page.</p>}
    </div>
  );
}
