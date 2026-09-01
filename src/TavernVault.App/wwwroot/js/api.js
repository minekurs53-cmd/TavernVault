// API 封装：统一错误处理 + 会话令牌（由 WebView2 注入 window.__TV_TOKEN__）

const authHeaders = () => ({ 'X-TV-Token': window.__TV_TOKEN__ || '' });

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

export const get = (url) => fetch(url, { headers: authHeaders() }).then(handle);
export const post = (url, body) => fetch(url, {
  method: 'POST',
  headers: { ...authHeaders(), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
  body: body !== undefined ? JSON.stringify(body) : undefined,
}).then(handle);
export const put = (url, body) => fetch(url, {
  method: 'PUT',
  headers: { ...authHeaders(), 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
}).then(handle);
export const del = (url, body) => fetch(url, {
  method: 'DELETE',
  headers: { ...authHeaders(), ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
  body: body !== undefined ? JSON.stringify(body) : undefined,
}).then(handle);

// img src 无法带自定义 header，走 ?token= query（服务端双通道收令牌）
export const imgSrc = (path) =>
  `${path}?token=${encodeURIComponent(window.__TV_TOKEN__ || '')}`;

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
  saveText: (id, content, expectedModified) => put(`/api/text/${id}`, { content, expectedModified }),
  favorite: (id, fav) => post(`/api/items/${id}/favorite`, { fav }),
  setTags: (id, tags) => post(`/api/items/${id}/tags`, { tags }),
  rename: (id, name, force = false) => post(`/api/items/${id}/rename`, { name, force }),
  move: (id, root, dir, force = false) => post(`/api/items/${id}/move`, { root, dir, force }),
  remove: (id) => post(`/api/items/${id}/delete`),
  reveal: (id) => post('/api/reveal', { id }),
  addRoot: (path, source = 'normal') => post('/api/roots', { path, source }),
  removeRoot: (path) => del('/api/roots', { path }),
  pickFolder: () => post('/api/pick-folder', {}),
  categories: () => get('/api/categories'),
  detectTavern: () => post('/api/tavern/detect'),
  connectTavern: (source) => post('/api/tavern/connect', { source }),
};
