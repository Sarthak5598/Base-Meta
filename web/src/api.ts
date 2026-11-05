import type { ConnectResponse, Connection, RowsPage, SchemaResponse, SortDirection } from './types';

/** An error with a message the API wrote for the user. */
export class ApiError extends Error {}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, init);
  } catch {
    throw new ApiError('Cannot reach the DataLens API. Is it running?');
  }

  const body = await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiError(body?.error ?? `Request failed (${response.status}).`);
  }
  return body as T;
}

export const api = {
  connections: () => request<Connection[]>('/api/connections'),

  connect: (name: string, connectionString: string) =>
    request<ConnectResponse>('/api/connections', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, connectionString }),
    }),

  schema: (connectionId: string) => request<SchemaResponse>(`/api/connections/${encodeURIComponent(connectionId)}/schema`),

  refresh: (connectionId: string) =>
    request<unknown>(`/api/connections/${encodeURIComponent(connectionId)}/schema/refresh`, { method: 'POST' }),

  rows: (
    connectionId: string,
    schema: string,
    table: string,
    page: number,
    pageSize: number,
    sort: string | null,
    dir: SortDirection,
  ) => {
    const query = new URLSearchParams({ schema, table, page: String(page), pageSize: String(pageSize), dir });
    if (sort) query.set('sort', sort);
    return request<RowsPage>(`/api/connections/${encodeURIComponent(connectionId)}/rows?${query}`);
  },
};
