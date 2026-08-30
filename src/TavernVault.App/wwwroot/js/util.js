// 通用工具：DOM、图标、格式化、Toast、弹窗

export function el(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}

export const $ = (sel, root = document) => root.querySelector(sel);

export function debounce(fn, ms) {
  let t;
  return (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
}

// ---- 图标（描边风格，viewBox 24）----
const PATHS = {
  all: 'M4 6h2v2H4zM10 6h10v2H10zM4 11h2v2H4zM10 11h10v2H10zM4 16h2v2H4zM10 16h10v2H10z',
  character: 'M12 11a4 4 0 1 0-4-4 4 4 0 0 0 4 4zm0 2c-4 0-7 2-7 5v2h14v-2c0-3-3-5-7-5z',
  lorebook: 'M4 5a2 2 0 0 1 2-2h13v16H6a2 2 0 0 0-2 2zm2 14h13M9 7h6M9 10h4',
  preset: 'M4 7h10M18 7h2M4 12h4M12 12h8M4 17h10M18 17h2M15 5v4M9 10v4M15 15v4',
  theme: 'M12 3a9 9 0 1 0 9 9c0-1.5-1-2-2-2h-2a2 2 0 0 1-2-2c0-1-.8-2-2-2zm-4.5 6a1.2 1.2 0 1 1 0 .1M8 14a1.2 1.2 0 1 1 0 .1M13 15a1.2 1.2 0 1 1 0 .1',
  script: 'M8 8l-4 4 4 4M16 8l4 4-4 4M13 5l-2 14',
  text: 'M6 3h9l4 4v14H6zM15 3v4h4M9 12h6M9 16h6',
  archive: 'M4 7h16v13H4zM4 4h16v3H4zM10 11h4',
  other: 'M6 3h9l4 4v14H6zM15 3v4h4',
  search: 'M10.5 17a6.5 6.5 0 1 1 0-13 6.5 6.5 0 0 1 0 13zM20 20l-4.5-4.5',
  grid: 'M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z',
  list: 'M4 6h16M4 12h16M4 18h16',
  refresh: 'M20 12a8 8 0 1 1-2.3-5.6M20 3v4h-4',
  star: 'M12 3.5l2.6 5.3 5.9.9-4.2 4.1 1 5.8-5.3-2.8-5.3 2.8 1-5.8L3.5 9.7l5.9-.9z',
  edit: 'M4 20h4L19.5 8.5a2.1 2.1 0 0 0-3-3L5 17zM14.5 7.5l3 3',
  folder: 'M3 6a1 1 0 0 1 1-1h5l2 2.5h9a1 1 0 0 1 1 1V19a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 14h10l1-14M10 11v6M14 11v6',
  x: 'M6 6l12 12M18 6L6 18',
  plus: 'M12 5v14M5 12h14',
  settings: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zm8-3a8 8 0 0 0-.2-1.7l2-1.5-2-3.4-2.3 1a8 8 0 0 0-3-1.7L14 2h-4l-.5 2.7a8 8 0 0 0-3 1.7l-2.3-1-2 3.4 2 1.5a8 8 0 0 0 0 3.4l-2 1.5 2 3.4 2.3-1a8 8 0 0 0 3 1.7L10 22h4l.5-2.7a8 8 0 0 0 3-1.7l2.3 1 2-3.4-2-1.5c.13-.55.2-1.12.2-1.7z',
  moon: 'M20 13.5A8.5 8.5 0 0 1 10.5 4 8.5 8.5 0 1 0 20 13.5z',
  sun: 'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M19.1 4.9L17 7M7 17l-2.1 2.1',
  tag: 'M3 11V4h7l10 10-7 7zM8 8.5h.01',
  copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
  move: 'M3 6a1 1 0 0 1 1-1h5l2 2.5h9a1 1 0 0 1 1 1V19a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1zm8 8h6m0 0l-2.5-2.5M17 14l-2.5 2.5',
  eye: 'M2 12s3.5-6.5 10-6.5S22 12 22 12s-3.5 6.5-10 6.5S2 12 2 12zm10 2.5a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z',
  check: 'M4 12.5l5 5L20 6.5',
  book: 'M12 6c-2-1.5-4.5-2-8-2v15c3.5 0 6 .5 8 2 2-1.5 4.5-2 8-2V4c-3.5 0-6 .5-8 2zm0 0v15',
};

export function icon(name) {
  const d = PATHS[name] || PATHS.other;
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="${d}"/></svg>`;
}

// 渲染所有 <span class="ico" data-icon="...">
export function hydrateIcons(root = document) {
  root.querySelectorAll('.ico[data-icon]').forEach((n) => {
    n.innerHTML = icon(n.dataset.icon);
  });
}

// ---- 格式化 ----
export function fmtSize(bytes) {
  if (bytes == null) return '';
  if (bytes < 1024) return bytes + ' B';
  if (bytes < 1048576) return (bytes / 1024).toFixed(1) + ' KB';
  if (bytes < 1073741824) return (bytes / 1048576).toFixed(1) + ' MB';
  return (bytes / 1073741824).toFixed(2) + ' GB';
}

export function fmtDate(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

// ---- Toast ----
export function toast(msg, kind = 'ok') {
  const root = document.getElementById('toast-root');
  const t = el(`<div class="toast ${kind}">${escapeHtml(msg)}</div>`);
  root.appendChild(t);
  setTimeout(() => {
    t.style.transition = 'opacity .25s';
    t.style.opacity = '0';
    setTimeout(() => t.remove(), 260);
  }, 2400);
}

// ---- 弹窗 ----
export function openModal(contentEl) {
  const mask = el('<div class="modal-mask"></div>');
  const modal = el('<div class="modal"></div>');
  modal.appendChild(contentEl);
  mask.appendChild(modal);
  mask.addEventListener('mousedown', (e) => { if (e.target === mask) mask.remove(); });
  document.getElementById('modal-root').appendChild(mask);
  return mask;
}

export function confirmDialog({ title, message, okText = '确定', danger = false }) {
  return new Promise((resolve) => {
    const body = el(`
      <div>
        <h3>${escapeHtml(title)}</h3>
        <p>${escapeHtml(message)}</p>
        <div class="m-actions">
          <button class="btn" data-act="cancel">取消</button>
          <button class="btn ${danger ? 'danger' : 'primary'}" data-act="ok">${escapeHtml(okText)}</button>
        </div>
      </div>`);
    const mask = openModal(body);
    let settled = false;
    const finish = (v) => {
      if (settled) return;
      settled = true;
      document.removeEventListener('keydown', onKey, true);
      mask.remove();
      resolve(v);
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); finish(false); } };
    document.addEventListener('keydown', onKey, true);
    body.addEventListener('click', (e) => {
      const act = e.target.closest('[data-act]')?.dataset.act;
      if (act) finish(act === 'ok');
    });
  });
}

export function promptDialog({ title, message = '', value = '', placeholder = '', okText = '确定' }) {
  return new Promise((resolve) => {
    const body = el(`
      <div>
        <h3>${escapeHtml(title)}</h3>
        ${message ? `<p>${escapeHtml(message)}</p>` : ''}
        <input type="text" value="${escapeHtml(value)}" placeholder="${escapeHtml(placeholder)}">
        <div class="m-actions">
          <button class="btn" data-act="cancel">取消</button>
          <button class="btn primary" data-act="ok">${escapeHtml(okText)}</button>
        </div>
      </div>`);
    const mask = openModal(body);
    const input = body.querySelector('input');
    input.focus();
    input.select();
    let settled = false;
    const finish = (v) => {
      if (settled) return;
      settled = true;
      document.removeEventListener('keydown', onKey, true);
      mask.remove();
      resolve(v);
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); finish(null); } };
    document.addEventListener('keydown', onKey, true);
    const done = (ok) => finish(ok ? input.value.trim() : null);
    body.addEventListener('click', (e) => {
      const act = e.target.closest('[data-act]')?.dataset.act;
      if (act) done(act === 'ok');
    });
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') done(true); });
  });
}
