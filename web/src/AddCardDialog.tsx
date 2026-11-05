import { useEffect, useRef, useState } from 'react';
import type { ChartType, SavedQuery } from './types';

interface TypeInfo {
  type: ChartType;
  title: string;
  blurb: string;
  icon: React.ReactNode;
}

// Small drawn icons: thin strokes in the current text colour, so they follow the theme.
const stroke = { fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round', strokeLinejoin: 'round' } as const;

export const CARD_TYPES: TypeInfo[] = [
  {
    type: 'line',
    title: 'Line chart',
    blurb: 'See how something changes over time.',
    icon: (
      <svg viewBox="0 0 32 32" width="28" height="28" aria-hidden="true" {...stroke}>
        <path d="M4 26 L4 5 M4 26 L28 26" />
        <path d="M8 20 L14 12 L19 17 L27 7" />
      </svg>
    ),
  },
  {
    type: 'bar',
    title: 'Bar chart',
    blurb: 'Compare categories side by side.',
    icon: (
      <svg viewBox="0 0 32 32" width="28" height="28" aria-hidden="true" {...stroke}>
        <path d="M4 26 L28 26" />
        <path d="M8 26 V16 M14 26 V8 M20 26 V13 M26 26 V19" strokeWidth="4" />
      </svg>
    ),
  },
  {
    type: 'pie',
    title: 'Pie chart',
    blurb: 'Show how a total splits into parts.',
    icon: (
      <svg viewBox="0 0 32 32" width="28" height="28" aria-hidden="true" {...stroke}>
        <circle cx="16" cy="16" r="11" />
        <path d="M16 16 L16 5 M16 16 L25.5 21.5" />
      </svg>
    ),
  },
  {
    type: 'table',
    title: 'Table',
    blurb: 'List rows, or a summary, as a table.',
    icon: (
      <svg viewBox="0 0 32 32" width="28" height="28" aria-hidden="true" {...stroke}>
        <rect x="4" y="6" width="24" height="20" rx="2" />
        <path d="M4 13 L28 13 M4 19.5 L28 19.5 M13 6 L13 26" />
      </svg>
    ),
  },
];

export const typeTitle = (t: ChartType) => CARD_TYPES.find((c) => c.type === t)?.title ?? 'card';

/** The four choices, as tiles. Used in the dialog and on an empty dashboard. */
export function TypeTiles({ onPick }: { onPick: (type: ChartType) => void }) {
  return (
    <div className="type-grid">
      {CARD_TYPES.map((c) => (
        <button key={c.type} type="button" className="type-tile" onClick={() => onPick(c.type)}>
          <span className="type-icon">{c.icon}</span>
          <span className="type-title">{c.title}</span>
          <span className="muted small">{c.blurb}</span>
        </button>
      ))}
    </div>
  );
}

interface Props {
  dashboardName: string;
  /** Saved questions that are not on this dashboard yet. */
  existing: SavedQuery[];
  onPickType: (type: ChartType) => void;
  onPickExisting: (queryId: string) => void;
  onClose: () => void;
}

/** "What do you want to add?" Choose a kind of card, or reuse a question you saved before. */
export function AddCardDialog({ dashboardName, existing, onPickType, onPickExisting, onClose }: Props) {
  const [pick, setPick] = useState('');
  const dialog = useRef<HTMLDivElement>(null);

  useEffect(() => {
    dialog.current?.querySelector<HTMLElement>('button')?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="add-card-title" ref={dialog}>
        <div className="dialog-head">
          <div>
            <h2 id="add-card-title">Add a card</h2>
            <p className="muted small">to {dashboardName}</p>
          </div>
          <button type="button" className="icon" aria-label="Close" onClick={onClose}>
            ×
          </button>
        </div>

        <TypeTiles onPick={onPickType} />

        {existing.length > 0 && (
          <div className="dialog-foot">
            <div className="foot-title">Or use a question you already saved</div>
            <div className="row">
              <select aria-label="A saved question" value={pick} onChange={(e) => setPick(e.target.value)}>
                <option value="">Choose a saved question…</option>
                {existing.map((q) => (
                  <option key={q.id} value={q.id}>
                    {q.name}
                  </option>
                ))}
              </select>
              <button type="button" disabled={!pick} onClick={() => onPickExisting(pick)}>
                Add it
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
