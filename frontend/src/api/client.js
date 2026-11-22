// Thin wrappers around the backend API (see design.md, API surface).

/** Error with the server's message (from ASP.NET Core problem details). */
export class ApiError extends Error {
  constructor(message, status) {
    super(message)
    this.status = status
  }
}

async function request(method, path, body) {
  let response
  try {
    response = await fetch(path, {
      method,
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError('Cannot reach the API. Is the backend running?', 0)
  }

  if (response.status === 204) return null

  const text = await response.text()
  const data = text ? safeJson(text) : null

  if (!response.ok) {
    throw new ApiError(problemMessage(data) ?? `Request failed (HTTP ${response.status})`, response.status)
  }
  return data
}

function safeJson(text) {
  try {
    return JSON.parse(text)
  } catch {
    return null
  }
}

/** Turns a problem details body into one readable message. */
function problemMessage(problem) {
  if (!problem) return null
  if (problem.errors) return Object.values(problem.errors).flat().join(' ')
  return problem.detail ?? problem.title ?? null
}

const query = (params) => {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== null && v !== '')
  return entries.length ? '?' + new URLSearchParams(entries).toString() : ''
}

export const api = {
  listUrls: () => request('GET', '/api/urls'),
  getUrl: (id) => request('GET', `/api/urls/${id}`),
  addUrl: (url, name) => request('POST', '/api/urls', { url, name }),
  deleteUrl: (id) => request('DELETE', `/api/urls/${id}`),
  checkNow: (id) => request('POST', `/api/urls/${id}/check`),
  getChecks: (id, { from, page, pageSize } = {}) =>
    request('GET', `/api/urls/${id}/checks${query({ from, page, pageSize })}`),
  getStats: (id, range) => request('GET', `/api/urls/${id}/stats${query({ range })}`),

  listRecipients: () => request('GET', '/api/alerts/recipients'),
  addRecipient: (email) => request('POST', '/api/alerts/recipients', { email }),
  deleteRecipient: (id) => request('DELETE', `/api/alerts/recipients/${id}`),
  sendTestEmail: () => request('POST', '/api/alerts/test'),
  listAlertEvents: (limit = 50) => request('GET', `/api/alerts/events${query({ limit })}`),
}
