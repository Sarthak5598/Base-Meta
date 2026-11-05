import { useEffect } from 'react';

/**
 * The question builder as a pop-up over the current page, so nothing is navigated away from.
 * Closed with the × or Cancel only: clicking outside or pressing Escape could throw away a half-built question.
 */
export function BuilderDialog({ onClose, children }: { onClose: () => void; children: React.ReactNode }) {
  // the page behind should not scroll while the pop-up is open
  useEffect(() => {
    const before = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = before;
    };
  }, []);

  return (
    <div className="backdrop builder-backdrop">
      <div className="dialog-xl" role="dialog" aria-modal="true" aria-label="Question builder">
        <button type="button" className="icon dialog-close" aria-label="Close" onClick={onClose}>
          ×
        </button>
        {children}
      </div>
    </div>
  );
}
