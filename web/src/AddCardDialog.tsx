import { useEffect, useRef, useState } from 'react';
import { describeQuery } from './query';
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
  /** Every saved question, so they can be reused here or deleted. */
  queries: SavedQuery[];
  /** The questions already on this dashboard. */
  onDashboard: string[];
  onPickType: (type: ChartType) => void;
  onPickExisting: (queryId: string) => void;
  onDelete: (queryId: string) => Promise<void>;
  onClose: () => void;
}

/**
 * "What do you want to add?" Choose a kind of card, or reuse a question saved before.
 * This is also where saved questions live: there is no separate page for them.
 */
export function AddCardDialog({ dashboardName, queries, onDashboard, onPickType, onPickExisting, onDelete, onClose }: Props) {
  const [confirming, setConfirming] = useState<string | null>(null);
  const dialog = useRef<HTMLDivElement>(null);

  // Focus the first choice once, when the dialog opens. (Not on every render: the page behind re-renders
  // as cards load, and that must not drag keyboard focus back to the top.)
  useEffect(() => {
    dialog.current?.querySelector<HTMLElement>('button')?.focus();
  }, []);

  // Escape closes. The handler reads the latest onClose without re-subscribing on every render.
  const closeRef = useRef(onClose);
  closeRef.current = onClose;
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') closeRef.current();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, []);

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

        {queries.length > 0 && (
          <div className="dialog-foot">
            <div className="foot-title">Or use a question you already saved</div>
            <ul className="saved-list">
              {queries.map((q) => {
                const there = onDashboard.includes(q.id);
                return (
                  <li key={q.id} className="saved-row">
                    <div className="saved-main">
                      <div className="saved-name">{q.name}</div>
                      <div className="muted small saved-desc">{describeQuery(q.query)}</div>
                    </div>
                    {confirming === q.id ? (
                      <div className="row">
                        <span className="small">Delete it{there ? ' and its card' : ''}?</span>
                        <button
                          type="button"
                          className="danger"
                          onClick={async () => {
                            await onDelete(q.id);
                            setConfirming(null);
                          }}
                        >
                          Yes
                        </button>
                        <button type="button" onClick={() => setConfirming(null)}>
                          Keep
                        </button>
                      </div>
                    ) : (
                      <div className="row">
                        <button type="button" disabled={there} onClick={() => onPickExisting(q.id)}>
                          {there ? 'On dashboard' : 'Add'}
                        </button>
                        <button type="button" aria-label={`Delete ${q.name}`} onClick={() => setConfirming(q.id)}>
                          Delete
                        </button>
                      </div>
                    )}
                  </li>
                );
              })}
            </ul>
          </div>
        )}
      </div>
    </div>
  );
}
