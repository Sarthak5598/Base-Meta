import type {
  ChartConfig,
  CompiledPreview,
  ConnectResponse,
  Connection,
  Dashboard,
  QueryDefinition,
  QuestionResult,
  RowsPage,
  SavedQuery,
  SchemaResponse,
  SortDirection,
} from './types';

/** An error with a message the API wrote for the user. */
export class ApiError extends Error {}

async function request<T>(path: string, init?: RequestInit, signal?: AbortSignal): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, { ...init, signal });
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e;
    throw new ApiError('Cannot reach the DataLens API. Is it running?');
  }

  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    throw new ApiError(body?.error ?? `Request failed (${response.status}).`);
  }
  return body as T;
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
});

const enc = encodeURIComponent;

export const api = {
  connections: () => request<Connection[]>('/api/connections'),

  connect: (name: string, connectionString: string) =>
    request<ConnectResponse>('/api/connections', json('POST', { name, connectionString })),

  schema: (connectionId: string) => request<SchemaResponse>(`/api/connections/${enc(connectionId)}/schema`),

  refresh: (connectionId: string) =>
    request<unknown>(`/api/connections/${enc(connectionId)}/schema/refresh`, { method: 'POST' }),

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
    return request<RowsPage>(`/api/connections/${enc(connectionId)}/rows?${query}`);
  },

  // ---- questions
  compile: (connectionId: string, query: QueryDefinition, signal?: AbortSignal) =>
    request<CompiledPreview>(`/api/connections/${enc(connectionId)}/query/compile`, json('POST', query), signal),

  run: (connectionId: string, query: QueryDefinition) =>
    request<QuestionResult>(`/api/connections/${enc(connectionId)}/query/run`, json('POST', query)),

  queries: () => request<SavedQuery[]>('/api/queries'),

  saveQuery: (connectionId: string, name: string, query: QueryDefinition, chart: ChartConfig) =>
    request<SavedQuery>('/api/queries', json('POST', { connectionId, name, query, chart })),

  updateQuery: (id: string, connectionId: string, name: string, query: QueryDefinition, chart: ChartConfig) =>
    request<SavedQuery>(`/api/queries/${enc(id)}`, json('PUT', { connectionId, name, query, chart })),

  deleteQuery: (id: string) => request<null>(`/api/queries/${enc(id)}`, { method: 'DELETE' }),

  runSaved: (id: string) =>
    request<{ query: SavedQuery; result: QuestionResult }>(`/api/queries/${enc(id)}/run`, json('POST', {})),

  // ---- dashboards
  dashboards: () => request<Dashboard[]>('/api/dashboards'),

  createDashboard: (name: string) => request<Dashboard>('/api/dashboards', json('POST', { name })),

  deleteDashboard: (id: string) => request<null>(`/api/dashboards/${enc(id)}`, { method: 'DELETE' }),

  addCard: (dashboardId: string, queryId: string) =>
    request<Dashboard>(`/api/dashboards/${enc(dashboardId)}/cards`, json('POST', { queryId })),

  removeCard: (dashboardId: string, queryId: string) =>
    request<Dashboard>(`/api/dashboards/${enc(dashboardId)}/cards/${enc(queryId)}`, { method: 'DELETE' }),
};
