// 主界面：侧栏、过滤、网格/列表、详情抽屉

import { api, imgSrc } from './api.js';
import {
  el, $, icon, hydrateIcons, toast, fmtSize, fmtDate, escapeHtml,
  confirmDialog, promptDialog, openModal,
} from './util.js';
import { openEditor, openBookEditor } from './editor.js';

export const state = {
  meta: null,
  items: [],
  // 筛选契约：filter 不持久化（刷新即重置）；仅 library 持久化到 localStorage('tv-library')
  filter: { library: 'normal', kind: null, q: '', tag: null, fav: false, dir: null, root: null, sort: 'name' },
  view: localStorage.getItem('tv-view') || 'grid',
  selectedId: null,
};

// 当前逻辑库（meta.libraries 里找，找不到回退 normal）
export function curLib() {
  const libs = state.meta?.libraries || [];
  return libs.find((l) => l.key === state.filter.library) || libs[0] || null;
}

// 切库：重置库内筛选（kind/dir/root/tag），保留搜索词/收藏/排序
export function switchLibrary(key) {
  Object.assign(state.filter, { library: key, kind: null, dir: null, root: null, tag: null });
  localStorage.setItem('tv-library', key);
  refreshItems();
}

// 侧栏手风琴：同一时间仅一个分区展开（null = 全部收起）
let openAcc = 'kind';
function applyAccordion() {
  ['kind', 'dir', 'tag'].forEach((k) => {
    $('#acc-' + k).classList.toggle('open', openAcc === k);
  });
}

function initAccordion() {
  ['kind', 'dir', 'tag'].forEach((k) => {
    $('#acc-' + k).querySelector('.acc-head').addEventListener('click', () => {
      const acc = $('#acc-' + k);
      if (acc.classList.contains('disabled')) return;
      openAcc = openAcc === k ? null : k;
      applyAccordion();
    });
  });
  applyAccordion();
}

const KIND_META = {
  character: { label: '角色卡', icon: 'character', color: '#d64f6f' },
  lorebook: { label: '世界书', icon: 'lorebook', color: '#b47909' },
  preset: { label: '预设', icon: 'preset', color: '#7458d6' },
  theme: { label: '美化', icon: 'theme', color: '#0a84ad' },
  script: { label: '脚本', icon: 'script', color: '#169160' },
  text: { label: '文本', icon: 'text', color: '#64748b' },
  archive: { label: '压缩包', icon: 'archive', color: '#92704f' },
  other: { label: '其他', icon: 'other', color: '#8a939e' },
};
export const kindMeta = (kind) => KIND_META[kind] || KIND_META.other;

// 展示名：卡片内名称优先，否则用文件名去扩展名
export const nameOf = (item) =>
  item.title || item.fileName.replace(/\.[^.]+$/, '');

// ============ 侧边栏 ============

export function renderSidebar() {
  const meta = state.meta;
  const nav = $('#nav');
  nav.innerHTML = '';

  const lib = curLib();
  const counts = {};
  (lib?.kinds || []).forEach((k) => { counts[k.kind] = k.count; });
  $('#brand-count').textContent = lib ? `共 ${lib.total} 个资源` : '索引中…';

  const mkItem = ({ label, ico, color, count, active, onClick }) => {
    const n = el(`
      <button class="nav-item ${active ? 'active' : ''}">
        ${color
          ? `<span class="dot" style="background:${color}"></span>`
          : `<span class="ico">${icon(ico)}</span>`}
        <span>${escapeHtml(label)}</span>
        <span class="count">${count ?? ''}</span>
      </button>`);
    n.addEventListener('click', onClick);
    return n;
  };

  // 三逻辑库选项卡（始终显示，含未接入的库）
  const tabs = $('#lib-tabs');
  tabs.innerHTML = '';
  (meta?.libraries || []).forEach((l) => {
    const badge = l.key === 'tavernST' ? '<span class="root-badge tavernST">ST</span>'
      : l.key === 'tavernTT' ? '<span class="root-badge tavernTT">TT</span>'
        : `<span class="ico">${icon('folder')}</span>`;
    const n = el(`
      <button class="lib-tab ${state.filter.library === l.key ? 'active' : ''}">
        ${badge}
        <span class="lib-name">${escapeHtml(l.label)}</span>
        <span class="count">${l.total}</span>
      </button>`);
    n.addEventListener('click', () => switchLibrary(l.key));
    tabs.appendChild(n);
  });

  // 类型分区：当前库内计数
  (lib?.kinds || []).forEach((k) => {
    const km = KIND_META[k.kind] || KIND_META.other;
    nav.appendChild(mkItem({
      label: km.label, color: km.color, count: k.count,
      active: state.filter.kind === k.kind,
      onClick: () => setFilter({ kind: k.kind, fav: false, tag: null }),
    }));
  });

  nav.appendChild(mkItem({
    label: '我的收藏', ico: 'star', count: lib?.favorites || '',
    active: state.filter.fav,
    onClick: () => setFilter({ kind: null, fav: !state.filter.fav, tag: null }),
  }));

  // 子目录二级导航：酒馆库按注册根（characters/worlds/...），普通库按相对目录
  const dirs = lib?.dirs || [];
  $('#acc-dir').classList.toggle('disabled', dirs.length === 0);
  const dirBox = $('#nav-dirs');
  dirBox.innerHTML = '';
  const isTavern = state.filter.library !== 'normal';
  dirs.forEach((d) => {
    const label = isTavern
      ? ((d.root || '').split(/[\\/]/).filter(Boolean).pop() || d.root)
      : (d.dir || '（根目录）');
    const active = isTavern ? state.filter.root === d.root : state.filter.dir === d.dir;
    const n = el(`
      <button class="nav-item ${active ? 'active' : ''}" title="${escapeHtml(isTavern ? d.root : d.dir)}">
        <span class="ico">${icon('folder')}</span>
        <span>${escapeHtml(label)}</span>
        <span class="count">${d.count}</span>
      </button>`);
    n.addEventListener('click', () => isTavern
      ? setFilter({ root: state.filter.root === d.root ? null : d.root, dir: null })
      : setFilter({ dir: state.filter.dir === d.dir ? null : d.dir, root: null }));
    dirBox.appendChild(n);
  });

  // 用户标签（当前库内计数）
  const tags = lib?.tags || [];
  $('#acc-tag').classList.toggle('disabled', tags.length === 0);
  const tagBox = $('#nav-tags');
  tagBox.innerHTML = '';
  tags.forEach((t) => {
    const n = el(`
      <button class="nav-item ${state.filter.tag === t.tag ? 'active' : ''}">
        <span class="ico">${icon('tag')}</span>
        <span>${escapeHtml(t.tag)}</span>
        <span class="count">${t.count}</span>
      </button>`);
    n.addEventListener('click', () => setFilter({ kind: null, fav: false, tag: state.filter.tag === t.tag ? null : t.tag }));
    tagBox.appendChild(n);
  });
}

function setFilter(patch) {
  Object.assign(state.filter, patch);
  refreshItems();
}

// ============ 列表 ============

let itemsSeq = 0; // 请求序号：并发刷新只有最新响应生效，防乱序覆盖 state.items（v0.5.2）

export async function refreshItems() {
  const seq = ++itemsSeq;
  $('#loading').hidden = false;
  try {
    const items = await api.items({
      kind: state.filter.kind || '',
      q: state.filter.q,
      tag: state.filter.tag || '',
      fav: state.filter.fav ? 'true' : '',
      source: state.filter.library || '',
      dir: state.filter.dir || '',
      root: state.filter.root || '',
      sort: state.filter.sort,
    });
    if (seq !== itemsSeq) return; // 已有更新的请求在途，丢弃过期响应（v0.5.2）
    state.items = items;
  } catch (e) {
    if (seq !== itemsSeq) return;
    toast(e.message, 'err');
    state.items = [];
  }
  $('#loading').hidden = true;
  renderContent();
  renderSidebar();
  // 抽屉打开时同步刷新
  if (state.selectedId && !$('#drawer-overlay').hidden) {
    const item = state.items.find((i) => i.id === state.selectedId);
    if (!item) closeDrawer();
  }
}

export function renderContent() {
  const grid = $('#grid');
  const empty = $('#empty');
  grid.innerHTML = '';
  const items = state.items;

  // 过滤条：库本身不算筛选（当前库名由品牌区体现），任一库内筛选激活才显示
  const f = state.filter;
  const lib = curLib();
  const scopeLabel = f.root
    ? `目录：${(f.root.split(/[\\/]/).filter(Boolean).pop() || f.root)}`
    : f.dir
      ? `目录：${f.dir}`
      : '';
  const label = f.q ? `搜索“${f.q}”`
    : f.tag ? `标签：${f.tag}`
      : f.fav ? '我的收藏'
        : scopeLabel;
  $('#filter-bar').hidden = !label;
  if (label) $('#filter-label').textContent = `${label} · ${items.length} 个结果`;

  if (!items.length) {
    empty.hidden = false;
    empty.innerHTML = '';
    // 空态优先级：未配置根 → 引导配置；已配置但无资源 → 重扫提示；有筛选无结果 → 筛选空态
    const noRoot = lib && lib.rootCount === 0;
    const noItems = lib && lib.rootCount > 0 && lib.total === 0;
    const heading = noRoot
      ? (state.filter.library === 'normal' ? '还没有添加库目录' : `还没有接入${lib.label}`)
      : noItems ? '该库还没有扫描到资源' : '这里空空如也';
    const hint = noRoot
      ? (state.filter.library === 'normal'
        ? '在库设置中添加资源文件夹，即可自动识别角色卡、世界书与预设。'
        : '在库设置中一键接入酒馆数据目录，即可在局外浏览与编辑酒馆资源。')
      : noItems ? '点击左侧「重新扫描」同步磁盘内容。' : '换个分类或关键词试试';
    const btn = noRoot ? '<button class="btn primary" id="btn-empty-settings">打开库设置</button>' : '';
    empty.appendChild(el(`
      <div>
        <div class="ico">${icon('folder')}</div>
        <h3>${heading}</h3>
        <div>${hint}</div>
        ${btn}
      </div>`));
    $('#btn-empty-settings', empty)?.addEventListener('click', () => {
      import('./main.js').then((m) => m.showSettings());
    });
    return;
  }
  empty.hidden = true;

  grid.classList.toggle('list', state.view === 'list');
  for (const item of items) {
    grid.appendChild(state.view === 'grid' ? renderCard(item) : renderRow(item));
  }
}

function tileStyle(kind) {
  const c = kindMeta(kind).color;
  return `background: color-mix(in srgb, ${c} 14%, var(--surface-2)); color:${c}`;
}

function renderCard(item) {
  const km = kindMeta(item.kind);
  const media = item.hasEmbeddedCard
    ? `<img loading="lazy" src="${imgSrc(`/api/thumb/${item.id}`)}" alt="" onerror="this.style.display='none'">`
    : `<div class="tile" style="${tileStyle(item.kind)}"><span class="ico">${icon(km.icon)}</span></div>`;
  const metaBits = [
    item.creator,
    item.entryCount ? `${item.entryCount} 条` : null,
    fmtSize(item.sizeBytes),
  ].filter(Boolean).join(' · ');

  const n = el(`
    <article class="card" data-id="${item.id}">
      <div class="thumb">
        ${media}
        <span class="badge k-${item.kind}">${km.label}</span>
        <button class="fav-btn ${item.favorite ? 'on' : ''}" title="收藏">
          <span class="ico">${icon('star')}</span>
        </button>
      </div>
      <div class="card-body">
        <h3>${escapeHtml(nameOf(item))}</h3>
        <p class="meta">${escapeHtml(metaBits)}</p>
      </div>
    </article>`);

  n.addEventListener('click', (e) => {
    if (e.target.closest('.fav-btn')) return;
    openDrawer(item.id);
  });
  n.querySelector('.fav-btn').addEventListener('click', async (e) => {
    e.stopPropagation();
    await toggleFavorite(item, n.querySelector('.fav-btn'));
  });
  return n;
}

function renderRow(item) {
  const km = kindMeta(item.kind);
  const media = item.hasEmbeddedCard
    ? `<img loading="lazy" src="${imgSrc(`/api/thumb/${item.id}`)}" alt="">`
    : `<span class="ico" style="color:${km.color}">${icon(km.icon)}</span>`;
  const n = el(`
    <div class="row" data-id="${item.id}">
      <div class="r-thumb">${media}</div>
      <h3>${escapeHtml(nameOf(item))}</h3>
      <div class="r-desc">${escapeHtml(item.description || item.relativeDir || '')}</div>
      <span class="kind-chip" style="background:${km.color}">${km.label}</span>
      <span class="r-meta">${fmtSize(item.sizeBytes)} · ${fmtDate(item.modifiedAt)}</span>
      <button class="fav-btn ${item.favorite ? 'on' : ''}"><span class="ico">${icon('star')}</span></button>
    </div>`);

  n.addEventListener('click', (e) => {
    if (e.target.closest('.fav-btn')) return;
    openDrawer(item.id);
  });
  n.querySelector('.fav-btn').addEventListener('click', async (e) => {
    e.stopPropagation();
    await toggleFavorite(item, n.querySelector('.fav-btn'));
  });
  return n;
}

async function toggleFavorite(item, btn) {
  try {
    await api.favorite(item.id, !item.favorite);
    item.favorite = !item.favorite;
    btn.classList.toggle('on', item.favorite);
    refreshItems();
  } catch (e) { toast(e.message, 'err'); }
}

// ============ 详情抽屉 ============

export async function openDrawer(id) {
  state.selectedId = id;
  const cached = state.items.find((i) => i.id === id);
  if (!cached) return;
  const overlay = $('#drawer-overlay');
  overlay.hidden = false;
  // 重取最新条目：409（文件已被外部修改）后缓存里的 modifiedAt 已过期，重开抽屉必须拿到新鲜时间戳（v0.5.2 N5）
  let item = cached;
  try {
    item = await api.item(id);
  } catch (e) { toast(e.message, 'err'); } // 取失败退回缓存条目渲染
  if (overlay.hidden) return; // 等待期间已被关闭（v0.5.2）
  renderDrawer(item);
  $('#drawer-overlay').querySelector('.drawer-close').focus();
}

export function closeDrawer() {
  $('#drawer-overlay').hidden = true;
  state.selectedId = null;
}

function renderDrawer(item) {
  const km = kindMeta(item.kind);
  const d = $('#drawer');
  d.innerHTML = '';

  const media = item.hasEmbeddedCard
    ? `<img src="${imgSrc(`/api/image/${item.id}`)}" alt="" onerror="this.style.display='none'">`
    : `<span class="ico" style="color:${km.color}">${icon(km.icon)}</span>`;

  const canEdit = ['character', 'lorebook', 'preset', 'theme', 'script', 'text'].includes(item.kind);
  const stats = [
    { k: '类型', v: `${km.label}${item.hasEmbeddedCard ? ' · 内嵌卡' : ''}` },
    { k: '大小', v: fmtSize(item.sizeBytes) },
    { k: '修改时间', v: fmtDate(item.modifiedAt) },
    ...(item.entryCount ? [{ k: '条目 / 提示词', v: String(item.entryCount) }] : []),
    { k: '所在目录', v: item.relativeDir || '（根目录）', wide: true },
    { k: '文件名', v: item.fileName, wide: true },
  ];

  const contentTags = item.tags || [];
  const userTags = item.userTags || [];

  const body = el(`
    <div class="drawer-wrap">
      <div class="drawer-head">
        <button class="icon-btn drawer-close" title="关闭 (Esc)"><span class="ico">${icon('x')}</span></button>
      </div>
      <div class="drawer-body">
        <div class="drawer-preview">${media}</div>
        <h2>${escapeHtml(nameOf(item))}</h2>
        <div class="drawer-sub">
          ${item.creator ? escapeHtml(item.creator) + ' · ' : ''}${item.version ? 'v' + escapeHtml(item.version) + ' · ' : ''}${km.label}
        </div>
        <div class="chips">
          <span class="chip accent">${km.label}</span>
          ${item.hasCharacterBook ? `<span class="chip" style="background:rgba(180,121,9,.16);color:#b47909">内置世界书 · ${item.entryCount} 条</span>` : ''}
          ${contentTags.map((t) => `<span class="chip">${escapeHtml(t)}</span>`).join('')}
          ${userTags.map((t) => `<span class="chip" style="border:1px dashed var(--accent);color:var(--accent)">我的：${escapeHtml(t)}</span>`).join('')}
        </div>
        ${item.description ? `<div class="drawer-desc">${escapeHtml(item.description)}</div>` : ''}
        <div class="user-tag-row">
          <input type="text" id="drawer-usertags" placeholder="我的标签（逗号分隔）" value="${escapeHtml(userTags.join(', '))}">
          <button class="btn sm" id="drawer-savetags">保存标签</button>
        </div>
        <div class="stat-grid">
          ${stats.map((s) => `<div class="stat ${s.wide ? 'wide' : ''}"><span>${s.k}</span><b title="${escapeHtml(s.v)}">${escapeHtml(s.v)}</b></div>`).join('')}
        </div>
        <div class="drawer-actions">
          ${canEdit ? `<button class="btn primary" data-act="edit"><span class="ico">${icon('edit')}</span>编辑</button>` : ''}
          ${item.hasCharacterBook ? `<button class="btn" data-act="editbook" style="grid-column:span 2;justify-content:center"><span class="ico">${icon('lorebook')}</span>编辑内置世界书（${item.entryCount} 条）</button>` : ''}
          <button class="btn" data-act="reveal"><span class="ico">${icon('folder')}</span>打开所在文件夹</button>
          <button class="btn" data-act="rename"><span class="ico">${icon('edit')}</span>重命名</button>
          <button class="btn" data-act="move"><span class="ico">${icon('move')}</span>移动到…</button>
          <button class="btn" data-act="copy"><span class="ico">${icon('copy')}</span>复制路径</button>
          <button class="btn" data-act="backups"><span class="ico">${icon('archive')}</span>备份与还原</button>
          <button class="btn danger" data-act="delete"><span class="ico">${icon('trash')}</span>删除（回收站）</button>
        </div>
      </div>
    </div>`);

  d.appendChild(body);
  hydrateIcons(d);

  body.querySelector('.drawer-close').addEventListener('click', closeDrawer);
  body.querySelector('#drawer-savetags').addEventListener('click', async () => {
    const raw = body.querySelector('#drawer-usertags').value;
    const tags = raw.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
    try {
      await api.setTags(item.id, tags);
      toast('标签已保存');
      refreshItems();
    } catch (e) { toast(e.message, 'err'); }
  });

  body.addEventListener('click', async (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act) return;
    try {
      if (act === 'edit') openEditor(item);
      if (act === 'editbook') openBookEditor(item);
      if (act === 'backups') showBackups(item);
      if (act === 'reveal') { await api.reveal(item.id); }
      if (act === 'copy') {
        await navigator.clipboard.writeText(item.fullPath);
        toast('路径已复制');
      }
      if (act === 'rename') {
        if (item.rootSource) {
          const yes = await confirmDialog({
            title: '酒馆文件重命名',
            message: '该文件来自酒馆目录，聊天通过文件名引用它。重命名后，引用该文件的酒馆聊天可能无法找到角色。确定继续？',
            okText: '仍然重命名', danger: true,
          });
          if (!yes) return;
        }
        const name = await promptDialog({
          title: '重命名', message: '只改文件名，保留扩展名。', value: nameOf(item),
        });
        if (name) {
          const r = await api.rename(item.id, name, !!item.rootSource);
          if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
          toast('已重命名');
          closeDrawer();
          await refreshMeta();
          await refreshItems();
          openDrawer(r.id);
        }
      }
      if (act === 'move') showMoveDialog(item);
      if (act === 'delete') {
        const yes = await confirmDialog({
          title: '删除资源',
          message: `“${nameOf(item)}” 将移入系统回收站，可在回收站恢复。`,
          okText: '删除', danger: true,
        });
        if (yes) {
          await api.remove(item.id);
          toast('已移入回收站');
          closeDrawer();
          refreshItems();
        }
      }
    } catch (err) { toast(err.message, 'err'); }
  });
}

// 备份与还原弹窗：列出该文件的自动备份，可还原/删除
async function showBackups(item) {
  const list = await api.backups(item.id);
  const body = el(`
    <div>
      <h3>备份与还原</h3>
      <p>“${escapeHtml(nameOf(item))}” 的历史备份（编辑保存前自动创建，份数可在库设置中调整）。</p>
      <div class="backup-list"></div>
      <div class="m-actions">
        <button class="btn" data-act="close">关闭</button>
      </div>
    </div>`);
  const mask = openModal(body);
  const box = body.querySelector('.backup-list');

  const renderList = (items) => {
    box.innerHTML = '';
    if (!items.length) {
      box.appendChild(el('<div class="empty" style="padding:16px">还没有备份——下次编辑保存时会自动创建</div>'));
      return;
    }
    for (const b of items) {
      const row = el(`<div class="backup-item">
        <span class="b-time">${escapeHtml(new Date(b.savedAt).toLocaleString())}</span>
        <span class="b-size">${fmtSize(b.sizeBytes)}</span>
        <button class="btn sm" data-bid="${b.id}" data-op="restore">还原</button>
        <button class="btn sm danger" data-bid="${b.id}" data-op="delete">删除</button>
      </div>`);
      row.addEventListener('click', async (e) => {
        const btn = e.target.closest('[data-op]');
        if (!btn) return;
        try {
          if (btn.dataset.op === 'restore') {
            const yes = await confirmDialog({
              title: '还原备份',
              message: `把文件还原到 ${new Date(b.savedAt).toLocaleString()} 的状态？当前文件会先自动备份一份。`,
              okText: '还原',
            });
            if (!yes) return;
            const r = await api.restoreBackup(b.id);
            if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
            toast('已还原');
            mask.remove();
            closeDrawer();
            await refreshMeta();
            await refreshItems();
            openDrawer(r.id);
          } else {
            await api.deleteBackup(b.id);
            renderList(await api.backups(item.id));
          }
        } catch (err) { toast(err.message, 'err'); }
      });
      box.appendChild(row);
    }
  };
  renderList(list);
  body.addEventListener('click', (e) => {
    if (e.target.closest('[data-act=close]')) mask.remove();
  });
}

async function showMoveDialog(item) {
  const cats = await api.categories();
  const roots = state.meta.roots || [];
  const checked = body => body.querySelector('input[name=mv-root]:checked')?.value || item.rootPath;
  const optionsFor = (rootPath) => cats.filter((c) => c.root === rootPath);
  // 目标根按来源分三组（条目所属来源的组排最前），跨库移动仍由后端护栏拦截
  const order = { normal: 0, tavernST: 1, tavernTT: 2 };
  const mySource = item.rootSource || 'normal';
  const groups = ['normal', 'tavernST', 'tavernTT']
    .map((s) => ({ source: s, roots: roots.filter((r) => (r.source || 'normal') === s) }))
    .filter((g) => g.roots.length > 0)
    .sort((a, b) => (a.source === mySource ? -1 : b.source === mySource ? 1 : order[a.source] - order[b.source]));
  const groupHtml = (g) => `
    <div class="m-section-title">${g.source === 'normal' ? '局外存储' : g.source === 'tavernST' ? 'SillyTavern' : 'TauriTavern'}</div>
    ${g.roots.map((r) => `
      <label style="display:flex;gap:8px;align-items:center;font-size:12.5px;padding:4px 2px">
        <input type="radio" name="mv-root" value="${escapeHtml(r.path)}" ${r.path === item.rootPath ? 'checked' : ''}>
        <span>${escapeHtml(r.path)}</span>
        ${r.source !== 'normal' ? `<span class="root-badge ${r.source}">${r.source === 'tavernST' ? 'ST' : 'TT'}</span>` : ''}
      </label>`).join('')}`;
  const catsHtml = (list) => list.map((c) => `
      <label><input type="radio" name="mv-dir" value="${escapeHtml(c.dir)}">
        <span>${c.dir ? escapeHtml(c.dir) : '（根目录）'}</span><span class="cnt">${c.count}</span>
      </label>`).join('') || '<p style="font-size:12px">暂无目录</p>';
  const body = el(`
    <div>
      <h3>移动“${escapeHtml(nameOf(item))}”</h3>
      <p>选择目标库根目录与子目录（不存在会自动创建）。</p>
      ${groups.map(groupHtml).join('')}
      <div class="m-section-title">常用目录</div>
      <div class="radio-list" id="mv-cats">
        ${catsHtml(optionsFor(item.rootPath))}
      </div>
      <input type="text" id="mv-custom" placeholder="或输入子目录，如：世界书/NSFW">
      <div class="m-actions">
        <button class="btn" data-act="cancel">取消</button>
        <button class="btn primary" data-act="ok">移动</button>
      </div>
    </div>`);

  const mask = openModal(body);
  // 切换目标根时，常用目录联动刷新
  body.querySelectorAll('input[name=mv-root]').forEach((r) => {
    r.addEventListener('change', () => {
      body.querySelector('#mv-cats').innerHTML = catsHtml(optionsFor(checked(body)));
    });
  });
  body.querySelector('#mv-custom').addEventListener('input', (e) => {
    if (e.target.value) body.querySelectorAll('input[name=mv-dir]').forEach((r) => { r.checked = false; });
  });
  body.addEventListener('click', async (e) => {
    const act = e.target.closest('[data-act]')?.dataset.act;
    if (!act) return;
    if (act === 'cancel') { mask.remove(); return; }
    const root = checked(body);
    const custom = body.querySelector('#mv-custom').value.trim();
    const dir = custom || body.querySelector('input[name=mv-dir]:checked')?.value || '';
    if (item.rootSource) {
      const yes = await confirmDialog({
        title: '酒馆文件移动',
        message: '该文件来自酒馆目录，移动后酒馆可能无法找到它（聊天通过路径引用）。确定继续？',
        okText: '仍然移动', danger: true,
      });
      if (!yes) return;
    }
    try {
      const r = await api.move(item.id, root, dir, !!item.rootSource);
      mask.remove();
      toast('已移动');
      closeDrawer();
      await refreshMeta();
      await refreshItems();
      openDrawer(r.id);
    } catch (err) { toast(err.message, 'err'); }
  });
}

// ============ 刷新入口 ============

export async function refreshMeta() {
  state.meta = await api.meta();
}

export function initShell() {
  // 侧栏手风琴（单开互斥）
  initAccordion();

  // 搜索
  const search = $('#search');
  let t;
  const syncClear = () => { $('#search-clear').hidden = !search.value; };
  search.addEventListener('input', () => {
    syncClear();
    clearTimeout(t);
    t = setTimeout(() => {
      state.filter.q = search.value.trim();
      refreshItems();
    }, 250);
  });
  $('#search-clear').addEventListener('click', () => {
    search.value = '';
    syncClear();
    state.filter.q = '';
    refreshItems();
  });

  // 排序
  $('#sort').addEventListener('change', (e) => {
    state.filter.sort = e.target.value;
    refreshItems();
  });

  // 视图切换
  document.querySelectorAll('#view-toggle button').forEach((b) => {
    b.classList.toggle('active', b.dataset.view === state.view);
    b.addEventListener('click', () => {
      state.view = b.dataset.view;
      localStorage.setItem('tv-view', state.view);
      document.querySelectorAll('#view-toggle button').forEach((x) =>
        x.classList.toggle('active', x === b));
      renderContent();
    });
  });

  // 主题
  $('#btn-theme').addEventListener('click', () => {
    const cur = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = cur;
    localStorage.setItem('tv-theme', cur);
    $('#btn-theme .ico').innerHTML = icon(cur === 'dark' ? 'sun' : 'moon');
  });

  // 全局快捷键
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      const mask = document.querySelector('.modal-mask');
      if (mask) { mask.remove(); return; } // 兜底：Esc 总能关掉弹窗（自带 Esc 处理的弹窗会先拦截）
      if (!$('#drawer-overlay').hidden) closeDrawer();
    }
    if ((e.ctrlKey || e.metaKey) && e.key === 'f') {
      e.preventDefault();
      $('#search').focus();
    }
  });

  $('#drawer-overlay').addEventListener('mousedown', (e) => {
    if (e.target.id === 'drawer-overlay') closeDrawer();
  });
}
