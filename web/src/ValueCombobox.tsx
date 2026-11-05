import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { api } from './api';
import type { FieldValues, TableRef } from './types';

interface Props {
  connectionId: string;
  table: TableRef;
  column: string;
  value: string;
  onChange: (value: string) => void;
  /** A comma separated list ("is one of"): picking adds to it and picking again takes out. */
  list?: boolean;
  placeholder?: string;
}

interface Option {
  text: string;
  close: boolean;
}

// What the server answered, kept for the session so going back to a word needs no round trip.
const cache = new Map<string, FieldValues>();

const tokens = (value: string) =>
  value
    .split(',')
    .map((t) => t.trim())
    .filter((t) => t.length > 0);

/**
 * A text box that suggests the values that really exist in the column, narrowing as you type and forgiving
 * typos ("shiped" offers "shipped" under "Did you mean"). Anything can still be typed: a suggestion is
 * a shortcut, never a rule.
 */
export function ValueCombobox({ connectionId, table, column, value, onChange, list = false, placeholder }: Props) {
  const id = useId();
  const listId = `${id}-list`;
  const root = useRef<HTMLDivElement>(null);

  const [open, setOpen] = useState(false);
  const [data, setData] = useState<FieldValues | null>(null);
  const [loading, setLoading] = useState(false);
  const [failed, setFailed] = useState(false);
  const [active, setActive] = useState(-1);

  // what is searched for: the whole box, or in a list the word after the last comma
  const term = (list ? value.slice(value.lastIndexOf(',') + 1) : value).trim();
  const chosen = useMemo(() => new Set(list ? tokens(value) : []), [list, value]);

  useEffect(() => {
    if (!open) return;
    const key = `${connectionId}|${table.schema}.${table.table}|${column}|${term.toLowerCase()}`;
    const hit = cache.get(key);
    if (hit) {
      setData(hit);
      setFailed(false);
      return;
    }

    const controller = new AbortController();
    setLoading(true);
    const timer = setTimeout(() => {
      api
        .values(connectionId, table.schema, table.table, column, term, controller.signal)
        .then((result) => {
          if (cache.size > 300) cache.clear();
          cache.set(key, result);
          setData(result);
          setFailed(false);
        })
        .catch((e) => {
          if (e instanceof DOMException && e.name === 'AbortError') return;
          setFailed(true);
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, 180);

    return () => {
      clearTimeout(timer);
      controller.abort();
      setLoading(false);
    };
  }, [open, connectionId, table.schema, table.table, column, term]);

  const options: Option[] = useMemo(
    () => [
      ...(data?.values ?? []).map((v) => ({ text: String(v), close: false })),
      ...(data?.close ?? []).map((v) => ({ text: String(v), close: true })),
    ],
    [data],
  );

  useEffect(() => setActive(-1), [options]);

  const pick = (o: Option) => {
    if (!list) {
      onChange(o.text);
      setOpen(false);
      return;
    }
    // in a list: take it out if it is there, otherwise it replaces the half-typed word
    const done = tokens(value.slice(0, value.lastIndexOf(',') + 1));
    const next = chosen.has(o.text) ? tokens(value).filter((t) => t !== o.text) : [...done, o.text];
    onChange(next.length ? next.join(', ') + ', ' : '');
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) return setOpen(true);
      if (options.length === 0) return;
      setActive((a) => (e.key === 'ArrowDown' ? (a + 1) % options.length : (a - 1 + options.length) % options.length));
    } else if (e.key === 'Enter' && open && active >= 0 && options[active]) {
      e.preventDefault();
      pick(options[active]);
    } else if (e.key === 'Escape' && open) {
      e.stopPropagation();
      setOpen(false);
    } else if (e.key === 'Tab') {
      setOpen(false);
    }
  };

  const firstClose = options.findIndex((o) => o.close);
  const shown = data?.values.length ?? 0;

  return (
    <div
      className="combo"
      ref={root}
      onBlur={(e) => {
        if (!root.current?.contains(e.relatedTarget as Node | null)) setOpen(false);
      }}
    >
      <input
        aria-label="Value"
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && active >= 0 ? `${id}-o${active}` : undefined}
        autoComplete="off"
        value={value}
        placeholder={placeholder}
        onFocus={() => setOpen(true)}
        onClick={() => setOpen(true)}
        onChange={(e) => {
          onChange(e.target.value);
          setOpen(true);
        }}
        onKeyDown={onKeyDown}
      />

      {open && (
        <ul className="combo-list" role="listbox" id={listId} aria-multiselectable={list || undefined}>
          {options.map((o, i) => (
            <li key={`${o.close}-${o.text}`} role="presentation">
              {i === firstClose && firstClose > 0 && <div className="combo-heading">Did you mean</div>}
              {i === firstClose && firstClose === 0 && <div className="combo-heading">No exact match. Did you mean</div>}
              <div
                id={`${id}-o${i}`}
                role="option"
                aria-selected={chosen.has(o.text)}
                className={`combo-option${i === active ? ' active' : ''}${chosen.has(o.text) ? ' chosen' : ''}`}
                onMouseDown={(e) => e.preventDefault()}   // keep the text box focused
                onClick={() => pick(o)}
                onMouseEnter={() => setActive(i)}
              >
                <span className="combo-tick" aria-hidden="true">
                  {chosen.has(o.text) ? '✓' : ''}
                </span>
                {o.text}
              </div>
            </li>
          ))}

          {loading && options.length === 0 && (
            <li role="presentation" className="combo-note">
              Loading…
            </li>
          )}
          {failed && (
            <li role="presentation" className="combo-note">
              Could not load suggestions. You can still type a value.
            </li>
          )}
          {!loading && !failed && options.length === 0 && (
            <li role="presentation" className="combo-note">
              {term ? 'Nothing like that in this column. You can still use what you typed.' : 'No values in this column.'}
            </li>
          )}
          {data?.hasMore && shown > 0 && (
            <li role="presentation" className="combo-note">
              Showing the first {shown}. Keep typing to narrow it down.
            </li>
          )}
        </ul>
      )}
    </div>
  );
}
