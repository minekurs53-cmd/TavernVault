// API 封装：统一错误处理

async function handle(resp) {
  if (resp.ok) {
    if (resp.status === 204) return null;
    const ct = resp.headers.get('content-type') || '';
    return ct.includes('json') ? resp.json() : resp.text();
  }
  let msg = `HTTP ${resp.status}`;
  try {
    const data = await resp.json();
    if (data && data.error) msg = data.error;
  } catch { /* ignore */ }
  throw new Error(msg);
}

export const get = (url) => fetch(url).then(handle);
export const post = (url, body) => fetch(url, {
  method: 'POST',
  headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
  body: body !== undefined ? JSON.stringify(body) : undefined,
}).then(handle);
export const put = (url, body) => fetch(url, {
  method: 'PUT',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
}).then(handle);
export const del = (url, body) => fetch(url, {
  method: 'DELETE',
  headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
  body: body !== undefined ? JSON.stringify(body) : undefined,
}).then(handle);

export const api = {
  meta: () => get('/api/meta'),
  items: (p = {}) => get('/api/items?' + new URLSearchParams(
    Object.entries(p).filter(([, v]) => v !== undefined && v !== null && v !== ''))),
  rescan: () => post('/api/rescan'),
  card: (id) => get(`/api/cards/${id}`),
  saveCard: (id, body) => put(`/api/cards/${id}`, body),
  cardBook: (id) => get(`/api/cards/${id}/book`),
  saveCardBook: (id, body) => put(`/api/cards/${id}/book`, body),
  saveCardAs: (id, card) => post(`/api/cards/${id}/saveas`, { card }),
  saveLoreAs: (id, entries) => post(`/api/lore/${id}/saveas`, { entries }),
  saveCardBookAs: (id, entries) => post(`/api/cards/${id}/book/saveas`, { entries }),
  saveTextAs: (id, content) => post(`/api/text/${id}/saveas`, { content }),
  backups: (id) => get(`/api/items/${id}/backups`),
  restoreBackup: (bid) => post(`/api/backups/${bid}/restore`, {}),
  deleteBackup: (bid) => del(`/api/backups/${bid}`),
  lore: (id) => get(`/api/lore/${id}`),
  saveLore: (id, body) => put(`/api/lore/${id}`, body),
  text: (id) => get(`/api/text/${id}`),
  saveText: (id, content) => put(`/api/text/${id}`, { content }),
  favorite: (id, fav) => post(`/api/items/${id}/favorite`, { fav }),
  setTags: (id, tags) => post(`/api/items/${id}/tags`, { tags }),
  rename: (id, name) => post(`/api/items/${id}/rename`, { name }),
  move: (id, root, dir) => post(`/api/items/${id}/move`, { root, dir }),
  remove: (id) => post(`/api/items/${id}/delete`),
  reveal: (id) => post('/api/reveal', { id }),
  addRoot: (path) => post('/api/roots', { path }),
  removeRoot: (path) => del('/api/roots', { path }),
  pickFolder: () => post('/api/pick-folder', {}),
  categories: () => get('/api/categories'),
};
