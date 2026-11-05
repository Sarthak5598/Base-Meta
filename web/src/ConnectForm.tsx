import { useState, type FormEvent } from 'react';

interface Props {
  onConnect: (name: string, connectionString: string) => Promise<void>;
  onCancel?: () => void;
}

export function ConnectForm({ onConnect, onCancel }: Props) {
  const [name, setName] = useState('');
  const [connectionString, setConnectionString] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await onConnect(name.trim(), connectionString.trim());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Something went wrong.');
      setBusy(false);
    }
  }

  return (
    <form className="connect" onSubmit={submit}>
      <h2>Connect a PostgreSQL database</h2>
      <p className="hint">
        DataLens reads the structure and shows rows. It never changes your data, but a read-only database user is
        still the safest choice.
      </p>

      <label htmlFor="conn-name">Name</label>
      <input
        id="conn-name"
        value={name}
        onChange={(e) => setName(e.target.value)}
        placeholder="shop"
        autoComplete="off"
        required
      />

      <label htmlFor="conn-string">Connection string</label>
      <input
        id="conn-string"
        className="mono"
        value={connectionString}
        onChange={(e) => setConnectionString(e.target.value)}
        placeholder="postgresql://user:password@host:5432/database"
        autoComplete="off"
        spellCheck={false}
        required
      />
      <p className="hint small">
        Also accepted: <code>Host=...;Port=5432;Database=...;Username=...;Password=...</code>. The password is stored
        encrypted and never sent back to the browser.
      </p>

      {error && (
        <div className="banner error" role="alert">
          {error}
        </div>
      )}

      <div className="actions">
        <button type="submit" className="primary" disabled={busy || !name.trim() || !connectionString.trim()}>
          {busy ? 'Connecting…' : 'Connect and read structure'}
        </button>
        {onCancel && (
          <button type="button" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
        )}
      </div>
    </form>
  );
}
