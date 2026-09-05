// 编辑器：角色卡（表单 / 原始JSON）、世界书（条目）、预设/美化/脚本/文本（原始JSON）

import { api } from './api.js';
import { el, $, icon, hydrateIcons, toast, escapeHtml, confirmDialog, openModal } from './util.js';
import { refreshItems, refreshMeta, kindMeta } from './app.js';
import { addPrompt, getGroups, isSystemPrompt, pickGroup, removePrompt, reorderGroup } from './preset-model.js';

let dirty = false;
let onCloseCleanup = null;
let tabController = null; // #editor-tabs 监听器生命周期：每次重建先 abort，防旧监听器累积（v0.5.2 P1-7）
let closing = false; // closeEditor 重入保护（v0.5.2 P1-10）
let saving = false; // 保存 in-flight 防抖（v0.5.2）

function markDirty() {
  dirty = true;
  document.getElementById('editor-overlay').classList.add('dirty');
}

function clearDirty() {
  dirty = false;
  document.getElementById('editor-overlay').classList.remove('dirty');
}

// ============ 打开 / 关闭 ============

// 搭建编辑器骨架（头部 + 快捷键），返回内容容器
function mountEditor(item, title, tabsHtml = '') {
  dirty = false;
  saveFn = null; // 清除上一会话残留；构建失败时保持 null，Ctrl+S 只提示不误写（v0.5.2）
  saveAsFn = null;
  saving = false; // 重置防抖，避免上一会话未完成的保存阻塞新会话（v0.5.2）
  const overlay = document.getElementById('editor-overlay');
  overlay.hidden = false;
  overlay.innerHTML = `
    <div class="editor-head">
      <button class="icon-btn editor-close" title="关闭 (Esc)"><span class="ico">${icon('x')}</span></button>
      <h2>${escapeHtml(title)}</h2>
      <span class="file-name">${escapeHtml(item.fileName)}</span>
      <span class="dirty-dot" title="有未保存的修改"></span>
      <div class="spacer"></div>
      ${tabsHtml}
      <button class="btn editor-saveas" title="把当前内容保存为新文件（自动命名）"><span class="ico">${icon('copy')}</span>另存为</button>
      <button class="btn primary editor-save"><span class="ico">${icon('check')}</span>保存 (Ctrl+S)</button>
    </div>
    <div class="editor-body" id="editor-body"><div class="empty">加载中…</div></div>`;
  hydrateIcons(overlay);

  overlay.querySelector('.editor-close').addEventListener('click', () => closeEditor());
  overlay.querySelector('.editor-save').addEventListener('click', () => doSave());
  overlay.querySelector('.editor-saveas').addEventListener('click', () => doSaveAs());

  onCloseCleanup = (e) => {
    // 捕获阶段拦截并阻断传播，避免底层抽屉/弹窗同时响应 Esc
    // 确认框悬空时交给其自身的 Esc 处理：本监听器先注册，直接返回防 Esc 级联重入（v0.5.2 P1-10）
    if (document.querySelector('.modal-mask')) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') e.preventDefault();
      return; // 悬空期间 Ctrl+S 也不写盘
    }
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      closeEditor();
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      e.preventDefault();
      e.stopPropagation();
      doSave();
    }
  };
  document.addEventListener('keydown', onCloseCleanup, true);
  return overlay.querySelector('#editor-body');
}

export async function openEditor(item) {
  if (item.rootSource) {
    // 酒馆来源就地编辑已退役（v0.7.1）：外部修改不会被酒馆实时读取，还可能被酒馆回写覆盖
    toast('酒馆来源文件不支持就地编辑，请在详情页使用「导出副本」', 'err');
    return;
  }
  const editable = ['character', 'lorebook', 'preset', 'theme', 'script', 'text'];
  if (!editable.includes(item.kind)) {
    toast('该类型暂不支持在程序内编辑', 'err');
    return;
  }

  const tabsHtml = `<div class="editor-tabs" id="editor-tabs" hidden></div>`;
  const body = mountEditor(item, `编辑 · ${kindMeta(item.kind).label}`, tabsHtml);

  try {
    if (item.kind === 'character') await buildCharacterEditor(item);
    else if (item.kind === 'lorebook') await buildLoreEditor(item);
    else if (item.kind === 'preset') await buildPresetEditor(item);
    else await buildRawEditor(item, item.kind);
  } catch (e) {
    disableSaveUi(); // 加载失败：禁用头部保存/另存为，saveFn 保持 null（v0.5.2）
    body.innerHTML = '';
    body.appendChild(el(`<div class="empty">加载失败：${escapeHtml(e.message)}</div>`));
  }
}

// 打开角色卡内嵌世界书编辑器（复用世界书条目编辑界面）
export async function openBookEditor(item) {
  const body = mountEditor(item, '编辑 · 内置世界书');
  try {
    await buildLoreEditor(item, { embedded: true });
  } catch (e) {
    disableSaveUi(); // 同 openEditor：加载失败禁用保存（v0.5.2）
    body.innerHTML = '';
    body.appendChild(el(`<div class="empty">加载失败：${escapeHtml(e.message)}</div>`));
  }
}

// 内嵌世界书合入（v0.7.6）：选库里的独立世界书 → 条目规范化追加进卡片内嵌书。
// 来源可以是酒馆来源（只读复制）；目标卡是酒馆来源时服务端 403（只读托管）。
async function showBookImport(item) {
  if (dirty && !(await confirmDialog({
    title: '有未保存的修改',
    message: '导入会重新加载内嵌世界书，未保存的修改将丢失。继续导入？',
    okText: '继续导入', danger: true,
  }))) return;

  let books;
  try {
    books = await api.items({ kind: 'lorebook' });
  } catch (e) { toast(e.message, 'err'); return; }
  if (!books.length) { toast('库里还没有独立世界书，可先「新建文件」或收纳入库', 'err'); return; }

  const cardName = item.title || item.fileName.replace(/\.[^.]+$/, '');
  const body = el(`
    <div>
      <h3>从独立世界书导入</h3>
      <p>选择来源，其全部条目将规范化后**追加**到「${escapeHtml(cardName)}」的内嵌世界书（现有条目不动）。</p>
      <div class="backup-list"></div>
      <div class="m-actions"><button class="btn" data-act="close">取消</button></div>
    </div>`);
  const mask = openModal(body);
  const box = body.querySelector('.backup-list');
  for (const b of books) {
    const row = el(`<div class="backup-item" style="cursor:pointer" title="点击导入">
      <span style="flex:1;min-width:0;font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${escapeHtml(b.title || b.fileName)}</span>
      ${b.rootSource ? '<span class="chip">酒馆</span>' : ''}
      <span class="b-size">${b.entryCount ?? 0} 条</span>
    </div>`);
    row.addEventListener('click', async () => {
      try {
        const r = await api.importCardBook(item.id, b.id);
        mask.remove();
        toast(`已合入 ${r.added} 个条目（现共 ${r.total} 条）`);
        await refreshMeta();
        await refreshItems();
        await openBookEditor(item); // 重载内嵌书展示新条目
      } catch (e) { toast(e.message, 'err'); }
    });
    box.appendChild(row);
  }
  body.addEventListener('click', (e) => {
    if (e.target.closest('[data-act=close]')) mask.remove();
  });
}

export async function closeEditor() {
  if (closing) return; // 重入保护：确认框悬空期间再次触发直接忽略（v0.5.2 P1-10）
  closing = true;
  try {
    if (dirty) {
      const ok = await confirmDialog({
        title: '放弃修改？',
        message: '当前有未保存的修改，关闭后将丢失。',
        okText: '放弃修改', danger: true,
      });
      if (!ok) return;
    }
    clearDirty();
    const overlay = document.getElementById('editor-overlay');
    overlay.hidden = true;
    overlay.innerHTML = '';
    if (onCloseCleanup) {
      document.removeEventListener('keydown', onCloseCleanup, true);
      onCloseCleanup = null;
    }
    tabController?.abort(); // 丢弃残留的 tab 监听器（v0.5.2 P1-7）
    tabController = null;
  } finally {
    closing = false;
  }
}

let saveFn = null;
let saveAsFn = null;
function doSave() {
  if (saving) return; // in-flight 防抖：连点只触发一次，第二枪会假 409（v0.5.2）
  if (document.querySelector('.modal-mask')) return; // 确认框悬空时不写盘（v0.5.2 P1-10）
  if (!saveFn) { toast('加载失败，无法保存', 'err'); return; } // 构建失败时保持 null（v0.5.2）
  saving = true;
  saveFn().finally(() => { saving = false; });
}
function doSaveAs() {
  if (!saveAsFn) { toast('当前视图不支持另存为', 'err'); return; }
  saveAsFn?.();
}

function bindDirty(root) {
  root.addEventListener('input', () => markDirty());
  root.addEventListener('change', () => markDirty());
}

// 加载失败后禁用头部保存/另存为按钮（v0.5.2）
function disableSaveUi() {
  const overlay = document.getElementById('editor-overlay');
  const save = overlay.querySelector('.editor-save');
  const saveAs = overlay.querySelector('.editor-saveas');
  if (save) save.disabled = true;
  if (saveAs) saveAs.disabled = true;
}

// 保存失败统一出口：409（文件已被外部修改）时自动重扫索引并提示用户重开（v0.5.2 N5）
async function handleSaveError(e) {
  if (String(e.message || '').includes('已被外部')) {
    await refreshItems();
    await refreshMeta();
    toast(e.message, 'err');
    toast('已重新扫描，请关闭后重新打开该条目再保存', 'err');
  } else {
    toast(e.message, 'err');
  }
}

// ============ 角色卡编辑 ============

const CARD_FIELDS = [
  { key: 'name', label: '名称', section: 'basic', type: 'input' },
  { key: 'creator', label: '创作者', section: 'basic', type: 'input' },
  { key: 'character_version', label: '版本', section: 'basic', type: 'input' },
  { key: 'description', label: '描述（角色设定）', section: 'content', type: 'textarea tall' },
  { key: 'personality', label: '性格', section: 'content', type: 'textarea' },
  { key: 'scenario', label: '场景', section: 'content', type: 'textarea' },
  { key: 'first_mes', label: '开场白', section: 'dialogue', type: 'textarea tall' },
  { key: 'mes_example', label: '示例对话', section: 'dialogue', type: 'textarea tall' },
  { key: 'creator_notes', label: '创作者备注', section: 'advanced', type: 'textarea' },
  { key: 'system_prompt', label: '系统提示', section: 'advanced', type: 'textarea' },
  { key: 'post_history_instructions', label: '对话后注入指令', section: 'advanced', type: 'textarea' },
];

async function buildCharacterEditor(item, preloaded = null) {
  // 每次重建换新 signal，移除旧 tab 监听器：旧监听器会操作已脱离 DOM 的表单并清模块级 dirty（v0.5.2 P1-7）
  tabController?.abort();
  tabController = new AbortController();
  const data = preloaded ? { card: preloaded } : await api.card(item.id);
  let card = data.card; // 完整卡片 JSON
  let dataNode = card.data || card;
  let refreshGreetings = null; // 备用开场白列表刷新入口，供保存后回填复用（v0.5.2 P1-8）

  const tabs = $('#editor-tabs');
  tabs.hidden = false;
  tabs.innerHTML = `
    <button data-tab="form" class="active">卡片表单</button>
    <button data-tab="raw">原始 JSON</button>`;

  const body = $('#editor-body');
  body.innerHTML = '';
  const formView = el(`<div class="form-scroll" style="flex:1"></div>`);
  const rawView = el(`<div class="raw-wrap" hidden></div>`);
  body.appendChild(formView);
  body.appendChild(rawView);

  // ---- 表单 ----
  const sections = {
    basic: { title: '基础信息', fields: [] },
    content: { title: '卡片内容', fields: [] },
    dialogue: { title: '对话', fields: [] },
    advanced: { title: '高级', fields: [] },
  };
  CARD_FIELDS.forEach((f) => sections[f.section].fields.push(f));

  const wrap = el('<div class="form-wrap"></div>');
  for (const [, sec] of Object.entries(sections)) {
    if (!sec.fields.length) continue;
    const cardEl = el(`<div class="form-card"><h4>${sec.title}</h4></div>`);
    sec.fields.forEach((f) => {
      const value = dataNode[f.key] ?? '';
      if (f.key === 'name' || f.key === 'creator' || f.key === 'character_version') {
        cardEl.appendChild(el(`<div class="field"><label>${f.label}</label>
          <input type="text" data-field="${f.key}" value="${escapeHtml(value)}"></div>`));
      } else {
        cardEl.appendChild(el(`<div class="field"><label>${f.label}</label>
          <textarea class="${f.type.includes('tall') ? 'tall' : ''}" data-field="${f.key}">${escapeHtml(value)}</textarea></div>`));
      }
    });

    if (sec.title === '对话') {
      const gaBox = el(`<div class="field"><label>备用开场白（<span class="ga-count">${(dataNode.alternate_greetings || []).length}</span>）</label><div class="greetings"></div>
        <button class="btn sm" data-add-greeting>＋ 添加备用开场白</button></div>`);
      const list = gaBox.querySelector('.greetings');
      const renderGreetings = () => {
        list.innerHTML = '';
        gaBox.querySelector('.ga-count').textContent = (dataNode.alternate_greetings || []).length;
        (dataNode.alternate_greetings || []).forEach((g, i) => {
          const item = el(`<div class="greeting-item">
            <textarea data-greeting="${i}">${escapeHtml(g)}</textarea>
            <button class="icon-btn" title="删除"><span class="ico">${icon('trash')}</span></button>
          </div>`);
          item.querySelector('textarea').addEventListener('input', (e) => {
            dataNode.alternate_greetings[i] = e.target.value;
            markDirty();
          });
          item.querySelector('button').addEventListener('click', () => {
            dataNode.alternate_greetings.splice(i, 1);
            markDirty();
            renderGreetings();
          });
          list.appendChild(item);
        });
      };
      renderGreetings();
      refreshGreetings = renderGreetings; // 暴露给保存后的回填（v0.5.2 P1-8）
      gaBox.querySelector('[data-add-greeting]').addEventListener('click', () => {
        if (!Array.isArray(dataNode.alternate_greetings)) dataNode.alternate_greetings = [];
        dataNode.alternate_greetings.push('');
        markDirty();
        renderGreetings();
      });
      cardEl.appendChild(gaBox);
    }
    wrap.appendChild(cardEl);
  }
  formView.appendChild(wrap);
  bindDirty(formView);

  // ---- 原始 JSON ----
  const raw = buildRawArea(JSON.stringify(card, null, 2), { json: true, flex: true });
  rawView.appendChild(raw.root);
  bindDirty(rawView);

  // { signal } 绑定：随 tabController 一起销毁，重建不再累积监听器（v0.5.2 P1-7）
  tabs.addEventListener('click', (e) => {
    const tab = e.target.closest('[data-tab]')?.dataset.tab;
    if (!tab) return;
    if (tab === 'raw') {
      if (dirty) {
        // 表单 → 原始视图：先把表单内容同步进 JSON
        applyFormToCard();
        raw.area.value = JSON.stringify(card, null, 2);
        clearDirty();
      }
      formView.hidden = true;
      rawView.hidden = false;
    } else {
      if (dirty) {
        // 原始视图 → 表单：解析最新 JSON 并重建表单；解析失败留在原文视图（v0.5.2）
        const parsed = tryParseJson(raw.area.value);
        if (!parsed) { toast('JSON 解析失败，已留在原文视图', 'err'); return; }
        card = parsed;
        dataNode = card.data || card;
        clearDirty();
        buildCharacterEditor(item, card);
        return;
      }
      formView.hidden = false;
      rawView.hidden = true;
    }
    // 切换成功后才更新高亮，解析失败时保持原文视图选中态（v0.5.2）
    tabs.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  }, { signal: tabController.signal });

  function applyFormToCard() {
    formView.querySelectorAll('[data-field]').forEach((inp) => {
      const key = inp.dataset.field;
      const v = inp.value.trim();
      if (v) dataNode[key] = v;
      else delete dataNode[key];
    });
  }

  saveFn = async () => {
    try {
      let payload;
      if (!rawView.hidden) {
        // 原始 JSON 模式：整卡替换
        const parsed = JSON.parse(raw.area.value);
        payload = { card: parsed };
      } else {
        applyFormToCard();
        payload = {
          fields: {},
          alternateGreetings: dataNode.alternate_greetings || [],
          tags: dataNode.tags || [],
        };
        CARD_FIELDS.forEach((f) => {
          const el2 = formView.querySelector(`[data-field="${f.key}"]`);
          if (el2) payload.fields[f.key] = el2.value;
        });
      }
      payload.expectedModified = item.modifiedAt; // 并发防护：文件被外部改动时服务端返回 409
      const r = await api.saveCard(item.id, payload);
      clearDirty();
      if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
      item.modifiedAt = r.modifiedAt;
      toast('已保存到 ' + (item.hasEmbeddedCard ? 'PNG 内嵌数据' : 'JSON 文件'));
      refreshItems();
      refreshMeta();
      // 重新加载以拿到服务端合并后的最新数据
      const fresh = await api.card(r.id || item.id);
      card = fresh.card;
      // 保存成功后互刷两个视图（对齐预设编辑器），防止之后从陈旧视图保存整体回滚本次结果（v0.5.2 P1-8）
      dataNode = card.data || card;
      raw.area.value = JSON.stringify(card, null, 2);
      formView.querySelectorAll('[data-field]').forEach((inp) => {
        inp.value = dataNode[inp.dataset.field] ?? '';
      });
      refreshGreetings?.();
    } catch (e) {
      if (e instanceof SyntaxError) toast('JSON 格式错误：' + e.message, 'err');
      else await handleSaveError(e); // 409 恢复路径（v0.5.2 N5）
    }
  };

  saveAsFn = async () => {
    try {
      let cardToSave;
      if (!rawView.hidden) {
        cardToSave = JSON.parse(raw.area.value); // 原始 JSON 模式
      } else {
        applyFormToCard();
        cardToSave = JSON.parse(JSON.stringify(card));
      }
      const r = await api.saveCardAs(item.id, cardToSave);
      clearDirty();
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
      closeEditor(); // 另存后关闭，避免继续编辑原文件造成误导（v0.5.2）
    } catch (e) {
      if (e instanceof SyntaxError) toast('JSON 格式错误：' + e.message, 'err');
      else await handleSaveError(e);
    }
  };
}

// ============ 世界书编辑 ============

const POSITION_OPTIONS = [
  ['0', '角色定义之前'], ['1', '角色定义之后'], ['2', '作者注释之前'],
  ['3', '作者注释之后'], ['4', '按深度插入'], ['5', '示例消息之前'], ['6', '示例消息之后'],
];

async function buildLoreEditor(item, opts = {}) {
  const embedded = !!opts.embedded; // 角色卡内嵌世界书模式
  const data = embedded ? await api.cardBook(item.id) : await api.lore(item.id);
  // v0.6.0 容器保形：独立世界书 GET 回传 container("object"/"array")，保存时原样带回
  const loreContainer = data.container;
  let entries = data.entries.map((e) => ({ key: e.key, data: e.data || {}, raw: e.raw }));
  let selected = 0;

  const body = $('#editor-body');
  body.innerHTML = `
    <div class="lore-layout">
      <div class="lore-list">
        <div class="lore-list-head">
          <div class="row1">
            <span class="count"><b class="lore-count">${entries.length}</b> 个条目</span>
            ${embedded ? `<button class="btn sm" data-import title="把库里独立世界书的条目追加进来"><span class="ico">${icon('lorebook')}</span>导入</button>` : ''}
            <button class="btn sm" data-add><span class="ico">${icon('plus')}</span>新增</button>
          </div>
          <input class="lore-search" type="text" placeholder="搜索条目…">
        </div>
        <div class="lore-items"></div>
      </div>
      <div class="lore-form"><div class="form-wrap"></div></div>
    </div>`;
  hydrateIcons(body);

  const listBox = body.querySelector('.lore-items');
  const searchInput = body.querySelector('.lore-search');
  const formWrap = body.querySelector('.lore-form .form-wrap');
  const countEl = body.querySelector('.lore-count');

  const entryTitle = (e) => e.data.comment || (e.data.key || []).join(', ') || `条目 ${e.key}`;
  const entryKeys = (e) => (e.data.key || []).join(', ');

  function renderList() {
    const q = (searchInput.value || '').toLowerCase();
    listBox.innerHTML = '';
    entries.forEach((e, i) => {
      const title = entryTitle(e);
      if (q && !(title + entryKeys(e) + (e.data.content || '')).toLowerCase().includes(q)) return;
      const n = el(`
        <div class="lore-item ${i === selected ? 'active' : ''}" data-i="${i}">
          <div class="li-title">${e.data.disable ? '<span class="li-off">[禁用]</span> ' : ''}${escapeHtml(title)}</div>
          <div class="li-keys">${escapeHtml(entryKeys(e) || '（无关键词）')}</div>
        </div>`);
      n.addEventListener('click', () => { selected = i; renderList(); renderForm(); });
      listBox.appendChild(n);
    });
    countEl.textContent = entries.length;
  }

  function renderForm() {
    const e = entries[selected];
    formWrap.innerHTML = '';
    if (!e) {
      formWrap.appendChild(el('<div class="empty">选择或新增一个条目</div>'));
      return;
    }
    const d = e.data;
    const num = (v) => (v ?? 0);
    const card = el(`
      <div class="form-card">
        <h4>条目 ${escapeHtml(e.key)}</h4>
        <div class="field"><label>备注 / 标题</label>
          <input type="text" data-k="comment" value="${escapeHtml(d.comment || '')}" placeholder="给条目起个名字"></div>
        <div class="field"><label>主关键词（逗号分隔，留空 = 常驻/手动触发）</label>
          <input type="text" data-k="key" value="${escapeHtml((d.key || []).join(', '))}"></div>
        <div class="field"><label>次要关键词（可选逻辑，逗号分隔）</label>
          <input type="text" data-k="keysecondary" value="${escapeHtml((d.keysecondary || []).join(', '))}"></div>
        <div class="field"><label>内容（注入给 AI 的文本）</label>
          <textarea class="tall" data-k="content">${escapeHtml(d.content || '')}</textarea></div>
        <div class="toggle-row">
          <label class="toggle"><input type="checkbox" data-k="constant" ${d.constant ? 'checked' : ''}>常驻（蓝灯）</label>
          <label class="toggle"><input type="checkbox" data-k="disable" ${d.disable ? 'checked' : ''}>禁用</label>
          <label class="toggle"><input type="checkbox" data-k="caseSensitive" ${d.caseSensitive ? 'checked' : ''}>区分大小写</label>
          <label class="toggle"><input type="checkbox" data-k="matchWholeWords" ${d.matchWholeWords !== false && d.matchWholeWords !== undefined ? 'checked' : ''}>整词匹配</label>
        </div>
        <div class="num-row">
          <div class="field"><label>插入顺序</label><input type="text" data-k="order" value="${escapeHtml(String(num(d.order)))}"></div>
          <div class="field"><label>插入位置</label>
            <select data-k="position">${POSITION_OPTIONS.map(([v, l]) =>
              `<option value="${v}" ${String(d.position ?? 0) === v ? 'selected' : ''}>${l}</option>`).join('')}</select></div>
          <div class="field"><label>深度</label><input type="text" data-k="depth" value="${escapeHtml(String(num(d.depth)))}"></div>
          <div class="field"><label>触发概率</label><input type="text" data-k="probability" value="${escapeHtml(String(num(d.probability ?? 100)))}"></div>
        </div>
        <button class="btn danger sm" data-del><span class="ico">${icon('trash')}</span>删除此条目</button>
      </div>`);

    card.querySelectorAll('[data-k]').forEach((inp) => {
      inp.addEventListener('input', () => {
        const k = inp.dataset.k;
        if (inp.type === 'checkbox') d[k] = inp.checked;
        else if (k === 'key' || k === 'keysecondary') {
          d[k] = inp.value.split(/[,，]/).map((s) => s.trim()).filter(Boolean);
        } else if (['order', 'depth', 'probability', 'position'].includes(k)) {
          const v = parseInt(inp.value, 10);
          d[k] = Number.isFinite(v) ? v : 0;
        } else d[k] = inp.value;
        markDirty();
      });
    });
    card.querySelector('[data-del]').addEventListener('click', async () => {
      const ok = await confirmDialog({ title: '删除条目', message: `删除“${entryTitle(e)}”？`, okText: '删除', danger: true });
      if (!ok) return;
      entries.splice(selected, 1);
      selected = Math.max(0, selected - 1);
      markDirty();
      renderList();
      renderForm();
    });

    formWrap.appendChild(card);
  }

  body.querySelector('[data-add]').addEventListener('click', () => {
    let maxKey = -1;
    entries.forEach((e) => {
      const n = parseInt(e.key, 10);
      if (Number.isFinite(n) && n > maxKey) maxKey = n;
    });
    entries.push({
      key: String(maxKey + 1),
      data: {
        key: [], keysecondary: [], content: '', comment: '新条目',
        constant: false, disable: false, order: 100, position: 0,
        depth: 4, probability: 100,
      },
    });
    selected = entries.length - 1;
    markDirty();
    renderList();
    renderForm();
  });

  // 内嵌世界书合入（v0.7.6）：从库里的独立世界书追加条目（仅内嵌模式显示）
  const importBtn = body.querySelector('[data-import]');
  if (importBtn) importBtn.addEventListener('click', () => showBookImport(item));

  searchInput.addEventListener('input', renderList);
  // 只绑表单区：搜索框输入不属于编辑内容，不应标脏（v0.5.2）
  bindDirty(body.querySelector('.lore-form'));

  renderList();
  renderForm();

  saveFn = async () => {
    try {
      if (embedded) {
        // raw（Spec 原条目）原样回传，服务端只合并被编辑的字段
        const r = await api.saveCardBook(item.id, {
          entries: entries.map((e) => ({ key: e.key, data: e.data, raw: e.raw })),
          expectedModified: item.modifiedAt,
        });
        clearDirty();
        if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
        item.modifiedAt = r.modifiedAt;
        toast(`内置世界书已保存（${r.count} 个条目）`);
        refreshItems();
        refreshMeta();
      } else {
        // container 原样回传：数组容器（Spec V2/NovelAI 导出）走服务端保形合并，不会被改写成对象
        const r = await api.saveLore(item.id, {
          entries: entries.map((e) => ({ key: e.key, data: e.data, raw: e.raw })),
          container: loreContainer,
          expectedModified: item.modifiedAt,
        });
        clearDirty();
        if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
        item.modifiedAt = r.modifiedAt;
        toast(`已保存（${r.count} 个条目）`);
        refreshItems();
        refreshMeta();
      }
    } catch (e) { await handleSaveError(e); } // 409 恢复路径（v0.5.2 N5）
  };

  saveAsFn = async () => {
    try {
      const payload = entries.map((e) => ({ key: e.key, data: e.data, raw: e.raw }));
      const r = embedded
        ? await api.saveCardBookAs(item.id, payload) // 导出为独立世界书
        : await api.saveLoreAs(item.id, payload);
      clearDirty();
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
      closeEditor(); // 另存后关闭，避免继续编辑原文件造成误导（v0.5.2）
    } catch (e) { await handleSaveError(e); }
  };
}

// ============ 预设可视化（B 计划二期：参数/开关/内容可编辑） ============

const SAMPLER_LABELS = {
  temperature: '温度', frequency_penalty: '频率惩罚', presence_penalty: '存在惩罚',
  top_p: 'Top P', top_k: 'Top K', top_a: 'Top A', min_p: 'Min P',
  repetition_penalty: '重复惩罚', open_max_context: '最大上下文解锁',
  max_context_unlocked: '最大上下文解锁', openai_max_tokens: '最大回复长度',
  openai_max_context: '上下文上限', openai_model: '模型', stream_openai: '流式输出',
  wrap_in_quotes: '引号包裹引文', names_behavior: '名字补充行为',
  squash_system_messages: '合并系统消息', continue_prefill: '续写预填充',
  continue_postfix: '续写后缀', continue_nudge_prompt: '续写提示',
  impersonation_prompt: '扮演提示', new_chat_prompt: '新对话提示',
  new_group_chat_prompt: '新群聊提示', new_example_chat_prompt: '新示例提示',
  bias_preset_selected: '偏好预设', function_calling: '函数调用',
  show_thoughts: '显示思考', reasoning_effort: '推理力度',
  enable_web_search: '联网搜索', request_images: '请求图片',
  image_inlining: '图片内联', inline_image_quality: '图片质量',
  assistant_prefill: '助手预填充', claude_use_sysprompt: 'Claude 系统提示',
  seed: '随机种子', n: '候选数', verbosity: '输出详细度',
  use_sysprompt: '使用系统提示', media_inlining: '媒体内联',
  prompts_usage: '提示词用法', maxattachments: '最大附件数',
};

// 常用参数优先展示的顺序，其余标量键按原顺序排在后面
const SAMPLER_ORDER = [
  'openai_model', 'temperature', 'frequency_penalty', 'presence_penalty',
  'top_p', 'top_k', 'top_a', 'min_p', 'repetition_penalty',
  'openai_max_tokens', 'openai_max_context', 'max_context_unlocked',
  'stream_openai', 'squash_system_messages', 'names_behavior',
  'reasoning_effort', 'show_thoughts', 'function_calling',
  'enable_web_search', 'continue_prefill', 'wrap_in_quotes', 'seed',
];

const ROLE_LABELS = { system: '系统', user: '用户', assistant: 'AI' };

function tryParseJson(text) {
  try {
    const v = JSON.parse(text);
    return v && typeof v === 'object' && !Array.isArray(v) ? v : null;
  } catch { return null; }
}

async function buildPresetEditor(item) {
  // 同角色卡编辑器：重建先移除旧 tab 监听器（v0.5.2 P1-7）
  tabController?.abort();
  tabController = new AbortController();
  let { content } = await api.text(item.id);
  let preset = tryParseJson(content);

  const tabs = $('#editor-tabs');
  tabs.hidden = false;
  tabs.innerHTML = `
    <button data-tab="visual" class="active">可视化</button>
    <button data-tab="raw">原文</button>`;

  const body = $('#editor-body');
  body.innerHTML = '';
  const visualView = el('<div class="form-scroll" style="flex:1"></div>');
  const rawView = el('<div class="raw-wrap" hidden></div>');
  body.appendChild(visualView);
  body.appendChild(rawView);

  const raw = buildRawArea(content, { json: true, flex: true });
  rawView.appendChild(raw.root);
  bindDirty(rawView);

  let visualActive = true;
  let rawStale = false; // 可视化侧有改动，原文文本已过期

  function touch() { rawStale = true; markDirty(); }

  // ---------- 可视化渲染 ----------
  function renderVisual() {
    visualView.innerHTML = '';
    if (!preset) {
      visualView.appendChild(el('<div class="empty">JSON 解析失败，请使用原文视图修正</div>'));
      return;
    }
    visualView.appendChild(buildParamsCard());
    visualView.appendChild(buildOrderCard());
    const extra = buildUnorderedCard();
    if (extra) visualView.appendChild(extra);
    visualView.appendChild(buildDetailCard());
    visualView.appendChild(buildStatsCard());
  }

  function scalarEntries() {
    const keys = Object.keys(preset).filter((k) => {
      const v = preset[k];
      return (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
        && !k.startsWith('_');
    });
    keys.sort((a, b) => {
      const ia = SAMPLER_ORDER.indexOf(a), ib = SAMPLER_ORDER.indexOf(b);
      return (ia === -1 ? 999 : ia) - (ib === -1 ? 999 : ib);
    });
    return keys.map((k) => [k, preset[k]]);
  }

  function buildParamsCard() {
    const card = el('<div class="form-card"><h4>采样与生成参数</h4><div class="param-grid"></div></div>');
    const grid = card.querySelector('.param-grid');
    for (const [k, v] of scalarEntries()) {
      const label = SAMPLER_LABELS[k] || k;
      const cell = el(`<div class="param" title="${escapeHtml(k)}"><span>${escapeHtml(label)}</span></div>`);
      const original = v;
      let input;
      if (typeof original === 'boolean') {
        input = el(`<label class="toggle"><input type="checkbox"> <span>${original ? '开' : '关'}</span></label>`);
        const cb = input.querySelector('input');
        cb.checked = original;
        cb.addEventListener('change', () => {
          preset[k] = cb.checked;
          input.querySelector('span').textContent = cb.checked ? '开' : '关';
          touch();
        });
      } else {
        input = el(`<input type="text" spellcheck="false">`);
        input.value = String(original);
        input.addEventListener('change', () => {
          if (typeof original === 'number') {
            const n = parseFloat(input.value);
            if (!Number.isFinite(n)) {
              toast(`“${label}”需要数字，已还原`, 'err');
              input.value = String(original);
              return;
            }
            preset[k] = n;
            input.value = String(n);
          } else {
            const t = input.value.trim();
            if (!t) { preset[k] = ''; input.value = ''; }
            else { preset[k] = t; input.value = t; }
          }
          touch();
        });
      }
      cell.appendChild(input);
      grid.appendChild(cell);
    }
    return card;
  }

  // 当前展示的排序分组（character_id 原值；getOrder 惰性解析并回写实际命中的值）
  let activeGroupId;
  let dragId = null; // 拖拽排序进行中的 identifier

  // v0.7.0 三期：分组选择交给 preset-model.pickGroup（精确 → 默认 100001 → 第一组）
  function getOrder() {
    const group = pickGroup(preset, activeGroupId);
    if (group) activeGroupId = group.character_id;
    return { group, orderList: group?.order ?? [] };
  }

  function groupLabel(g) {
    return g.character_id === 100001 ? `默认角色 (${g.character_id})` : `角色 ${g.character_id}`;
  }

  // 删除提示词（生效序列与未排序清单共用）：确认 → prompts 与所有分组 order 同步移除
  async function deletePromptFlow(identifier, name) {
    const yes = await confirmDialog({
      title: '删除提示词',
      message: `确定从预设中删除「${name}」？将同时从所有排序分组中移除。`,
      okText: '删除', danger: true,
    });
    if (!yes) return;
    const r = removePrompt(preset, identifier);
    if (!r.ok) {
      toast(r.reason === 'system' ? '系统管理项不可删除' : '未找到该提示词', 'err');
      return;
    }
    touch();
    renderVisual();
  }

  function buildOrderCard() {
    const prompts = Array.isArray(preset.prompts) ? preset.prompts : [];
    const byId = new Map(prompts.map((p) => [p.identifier, p]));
    const groups = getGroups(preset);
    const { orderList } = getOrder();

    const card = el(`<div class="form-card">
      <div class="po-toolbar">
        <h4>生效顺序 · 勾选启用 / 拖拽排序</h4>
        ${groups.length > 1 ? '<select class="po-group" title="排序分组（character_id）"></select>' : ''}
        <button class="btn sm po-add"><span class="ico">${icon('plus')}</span>新增提示词</button>
      </div>
      <div class="preset-list"></div>
      <div class="po-addform" hidden>
        <div class="po-addrow">
          <input class="af-name" placeholder="名称（留空则用「新提示词」）">
          <select class="af-role">
            <option value="system">system（系统）</option>
            <option value="user">user（用户）</option>
            <option value="assistant">assistant（AI）</option>
          </select>
        </div>
        <textarea class="af-content pd-edit" rows="4" placeholder="提示词内容（创建后可在下方详情继续编辑）"></textarea>
        <div class="po-addrow">
          <button class="btn primary sm af-ok"><span class="ico">${icon('check')}</span>创建</button>
          <button class="btn sm af-cancel">取消</button>
        </div>
      </div>
    </div>`);
    const list = card.querySelector('.preset-list');

    // 角色分组切换（>1 组才显示；增删/排序作用于当前选中分组）
    const groupSel = card.querySelector('.po-group');
    if (groupSel) {
      groupSel.innerHTML = groups
        .map((g) => `<option value="${escapeHtml(String(g.character_id))}">${escapeHtml(groupLabel(g))}</option>`)
        .join('');
      groupSel.value = String(activeGroupId);
      groupSel.addEventListener('change', () => {
        const g = groups.find((x) => String(x.character_id) === groupSel.value);
        activeGroupId = g?.character_id;
        renderVisual();
      });
    }

    // 新增提示词：写入 prompts[] + 当前分组 order[] 末尾（enabled:true）
    const addForm = card.querySelector('.po-addform');
    card.querySelector('.po-add').addEventListener('click', () => {
      addForm.hidden = !addForm.hidden;
      if (!addForm.hidden) addForm.querySelector('.af-name').focus();
    });
    addForm.querySelector('.af-cancel').addEventListener('click', () => { addForm.hidden = true; });
    addForm.querySelector('.af-ok').addEventListener('click', () => {
      const created = addPrompt(preset, activeGroupId, {
        name: addForm.querySelector('.af-name').value.trim(),
        role: addForm.querySelector('.af-role').value,
        content: addForm.querySelector('.af-content').value,
      });
      if (!created) { toast('该预设缺少 prompts 数组，无法新增', 'err'); return; }
      touch();
      toast('已新增（保存后写入文件）');
      renderVisual();
      // 直接选中新行，便于立即编辑内容
      visualView.querySelector(`.preset-row[data-identifier="${CSS.escape(created.identifier)}"]`)?.click();
    });

    orderList.forEach((o, idx) => {
      const p = byId.get(o.identifier);
      const isMarker = isSystemPrompt(p);
      const name = p?.name || o.identifier;
      // role 来自第三方预设文件内容（不可信），非标准取值回退原值时必须转义（v0.5.1 XSS 修复）
      const role = isMarker ? '系统' : (p?.role ? (ROLE_LABELS[p.role] || escapeHtml(p.role)) : '—');
      const len = p?.content ? p.content.length : 0;

      const row = el(`<div class="preset-row ${o.enabled ? '' : 'off'}" draggable="true" data-identifier="${escapeHtml(o.identifier)}">
        <span class="po-drag" title="拖拽排序">⋮⋮</span>
        <span class="po-idx">${idx + 1}</span>
        <input type="checkbox" class="po-cb" title="启用/禁用">
        <span class="po-name">${escapeHtml(name)}</span>
        <span class="po-role">${role}</span>
        <span class="po-len">${len ? len + ' 字' : ''}</span>
        <button class="po-del" title="${isMarker ? '系统管理项不可删除' : '删除提示词'}" ${isMarker ? 'disabled' : ''}><span class="ico">${icon('trash')}</span></button>
      </div>`);
      const cb = row.querySelector('.po-cb');
      cb.checked = !!o.enabled;
      cb.addEventListener('change', () => {
        o.enabled = cb.checked;
        row.classList.toggle('off', !cb.checked);
        touch();
        updateStats();
      });
      row.addEventListener('click', (e) => {
        if (e.target === cb) return;
        list.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('sel'));
        row.classList.add('sel');
        renderPresetDetail(p, o.identifier, (newName, newLen) => {
          row.querySelector('.po-name').textContent = newName || o.identifier;
          row.querySelector('.po-len').textContent = newLen ? newLen + ' 字' : '';
        });
      });
      row.querySelector('.po-del').addEventListener('click', (e) => {
        e.stopPropagation();
        if (isMarker) return;
        deletePromptFlow(o.identifier, name);
      });

      // 拖拽排序：dragover 按上半/下半计算插入位，drop 按新次序写回当前分组 order
      //（只重排数组、沿用原对象引用，enabled 与 prompts[] 本体不动——保形见 preset-model 单测）
      row.addEventListener('dragstart', (e) => {
        dragId = o.identifier;
        row.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', String(o.identifier));
      });
      row.addEventListener('dragend', () => {
        dragId = null;
        row.classList.remove('dragging');
        list.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('drop-above', 'drop-below'));
      });
      row.addEventListener('dragover', (e) => {
        if (!dragId || dragId === o.identifier) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        list.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('drop-above', 'drop-below'));
        const rect = row.getBoundingClientRect();
        row.classList.add(e.clientY < rect.top + rect.height / 2 ? 'drop-above' : 'drop-below');
      });
      row.addEventListener('drop', (e) => {
        if (!dragId || dragId === o.identifier) return;
        e.preventDefault();
        const before = row.classList.contains('drop-above');
        const dragged = orderList.find((x) => x.identifier === dragId);
        if (!dragged) return;
        const ids = orderList.map((x) => x.identifier).filter((id) => id !== dragId);
        let at = ids.indexOf(o.identifier);
        if (!before) at++;
        ids.splice(at, 0, dragId);
        reorderGroup(preset, activeGroupId, ids.map((id) => orderList.find((x) => x.identifier === id)));
        dragId = null;
        touch();
        renderVisual();
      });

      list.appendChild(row);
    });
    return card;
  }

  function buildUnorderedCard() {
    const prompts = Array.isArray(preset.prompts) ? preset.prompts : [];
    const { orderList } = getOrder();
    const orderedIds = new Set(orderList.map((o) => o.identifier));
    const unordered = prompts.filter((p) => !orderedIds.has(p.identifier));
    if (!unordered.length) return null;

    const card = el('<div class="form-card"><h4>未加入生效序列的提示词</h4><div class="preset-list"></div></div>');
    const list = card.querySelector('.preset-list');
    unordered.forEach((p) => {
      const isMarker = isSystemPrompt(p);
      const name = p.name || p.identifier;
      const row = el(`<div class="preset-row" data-identifier="${escapeHtml(p.identifier)}">
        <span class="po-drag ghost">⋮⋮</span>
        <span class="po-idx">·</span>
        <span class="po-cb"></span>
        <span class="po-name">${escapeHtml(name)}</span>
        <span class="po-role">${isMarker ? '系统' : (ROLE_LABELS[p.role] || escapeHtml(p.role) || '—')}</span>
        <span class="po-len">${p.content ? p.content.length + ' 字' : ''}</span>
        <button class="po-del" title="${isMarker ? '系统管理项不可删除' : '删除提示词'}" ${isMarker ? 'disabled' : ''}><span class="ico">${icon('trash')}</span></button>
      </div>`);
      row.addEventListener('click', (e) => {
        if (e.target.closest('.po-del')) return;
        list.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('sel'));
        row.classList.add('sel');
        renderPresetDetail(p, p.identifier, (newName, newLen) => {
          row.querySelector('.po-name').textContent = newName || p.identifier;
          row.querySelector('.po-len').textContent = newLen ? newLen + ' 字' : '';
        });
      });
      row.querySelector('.po-del').addEventListener('click', (e) => {
        e.stopPropagation();
        if (isMarker) return;
        deletePromptFlow(p.identifier, name);
      });
      list.appendChild(row);
    });
    return card;
  }

  function buildDetailCard() {
    const card = el('<div class="form-card"><h4>提示词内容（点击上方条目编辑）</h4><div class="preset-detail"><div class="empty" style="padding:14px">尚未选择</div></div></div>');
    detailBox = card.querySelector('.preset-detail');
    return card;
  }

  function buildStatsCard() {
    const card = el('<div class="form-card"><h4>统计</h4><div class="param-grid stats-grid"></div></div>');
    statsGrid = card.querySelector('.stats-grid');
    updateStats();
    return card;
  }

  let detailBox = null;
  let statsGrid = null;

  function updateStats() {
    if (!statsGrid) return;
    const prompts = Array.isArray(preset.prompts) ? preset.prompts : [];
    const { orderList } = getOrder();
    const orderedIds = new Set(orderList.map((o) => o.identifier));
    const enabled = orderList.filter((o) => o.enabled).length;
    const unordered = prompts.filter((p) => !orderedIds.has(p.identifier)).length;
    const groups = Array.isArray(preset.prompt_order) ? preset.prompt_order.length : 0;
    statsGrid.innerHTML = `
      <div class="param"><span>提示词总数</span><b>${prompts.length}</b></div>
      <div class="param"><span>生效序列</span><b>${orderList.length}（启用 ${enabled}）</b></div>
      <div class="param"><span>未排序</span><b>${unordered}</b></div>
      <div class="param"><span>排序组</span><b>${groups}</b></div>`;
  }

  function renderPresetDetail(p, identifier, onRowUpdate) {
    if (!detailBox) return;
    if (!p) {
      detailBox.innerHTML = `<div class="empty" style="padding:14px">提示词 "${escapeHtml(identifier)}" 在 prompts 列表中不存在（可能已被删除）</div>`;
      return;
    }
    const isMarker = p.marker === true || p.system_prompt === true;
    detailBox.innerHTML = `
      <div class="pd-head">
        <input class="pd-name" placeholder="名称" ${isMarker ? 'disabled title="系统管理项"' : ''}>
        <span class="chip accent">${escapeHtml(identifier)}</span>
      </div>
      <div class="pd-meta"></div>
      ${!isMarker ? `
      <div class="pd-inject">
        <label>注入：
          <select class="pd-pos">
            <option value="relative">相对位置</option>
            <option value="depth">按深度</option>
          </select>
        </label>
        <label class="pd-depth-wrap" hidden>深度 <input type="text" class="pd-depth"></label>
        <label class="toggle" style="margin-left:auto"><input type="checkbox" class="pd-fobid"> 禁止覆盖</label>
      </div>` : ''}
      <textarea class="pd-content pd-edit" spellcheck="false" ${isMarker ? 'disabled title="系统管理项内容不可编辑"' : ''}></textarea>`;

    const nameInput = detailBox.querySelector('.pd-name');
    const contentArea = detailBox.querySelector('.pd-content');
    nameInput.value = p.name || '';
    contentArea.value = p.content || '';

    const meta = [];
    if (isMarker) meta.push('系统管理项（内容不可编辑）');
    if (p.role) meta.push('角色：' + (ROLE_LABELS[p.role] || p.role));
    detailBox.querySelector('.pd-meta').textContent = meta.join(' · ') || '—';

    if (!isMarker) {
      nameInput.addEventListener('input', () => {
        p.name = nameInput.value;
        touch();
        onRowUpdate(p.name, p.content ? p.content.length : 0);
      });
      contentArea.addEventListener('input', () => {
        p.content = contentArea.value;
        touch();
        onRowUpdate(p.name, p.content.length);
      });

      const posSel = detailBox.querySelector('.pd-pos');
      const depthWrap = detailBox.querySelector('.pd-depth-wrap');
      const depthInput = detailBox.querySelector('.pd-depth');
      const forbidCb = detailBox.querySelector('.pd-fobid');
      const isDepth = p.injection_position === 1;
      posSel.value = isDepth ? 'depth' : 'relative';
      depthWrap.hidden = !isDepth;
      if (isDepth) depthInput.value = String(p.injection_depth ?? 4);
      forbidCb.checked = !!p.forbid_overrides;

      posSel.addEventListener('change', () => {
        const d = posSel.value === 'depth';
        p.injection_position = d ? 1 : 0;
        if (d && p.injection_depth === undefined) p.injection_depth = 4;
        depthWrap.hidden = !d;
        if (d) depthInput.value = String(p.injection_depth ?? 4);
        touch();
      });
      depthInput.addEventListener('change', () => {
        const n = parseInt(depthInput.value, 10);
        if (!Number.isFinite(n)) { depthInput.value = String(p.injection_depth ?? 4); return; }
        p.injection_depth = n;
        touch();
      });
      forbidCb.addEventListener('change', () => { p.forbid_overrides = forbidCb.checked; touch(); });
    }
  }

  renderVisual();

  // ---------- 视图切换与保存 ----------
  tabs.addEventListener('click', (e) => {
    const tab = e.target.closest('[data-tab]')?.dataset.tab;
    if (!tab) return;
    if ((tab === 'visual') === visualActive) return;
    if (tab === 'raw') {
      if (rawStale) {
        raw.area.value = JSON.stringify(preset, null, 2);
        rawStale = false;
      }
      visualActive = false;
    } else {
      const parsed = tryParseJson(raw.area.value);
      if (!parsed) {
        toast('JSON 解析失败，已留在原文视图', 'err');
        return;
      }
      preset = parsed;
      rawStale = false;
      visualActive = true;
      renderVisual();
    }
    visualView.hidden = !visualActive;
    rawView.hidden = visualActive;
    tabs.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  }, { signal: tabController.signal }); // 随 tabController 一起销毁（v0.5.2 P1-7）

  const currentText = () => {
    if (visualActive) return JSON.stringify(preset, null, 2);
    if (!raw.validate()) throw new Error('JSON 校验失败，请先修正');
    return raw.area.value;
  };

  saveFn = async () => {
    let text;
    try { text = currentText(); } catch (err) { toast(err.message, 'err'); return; }
    try {
      const r = await api.saveText(item.id, text, item.modifiedAt);
      clearDirty();
      rawStale = false;
      if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
      item.modifiedAt = r.modifiedAt;
      toast('已保存（保存前已自动备份）');
      refreshItems();
      refreshMeta();
      const fresh = await api.text(item.id);
      content = fresh.content;
      preset = tryParseJson(content);
      raw.area.value = content;
      if (visualActive) renderVisual();
    } catch (e) { await handleSaveError(e); } // 409 恢复路径（v0.5.2 N5）
  };

  saveAsFn = async () => {
    let text;
    try { text = currentText(); } catch (err) { toast(err.message, 'err'); return; }
    try {
      const r = await api.saveTextAs(item.id, text);
      clearDirty();
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
      closeEditor(); // 另存后关闭，避免继续编辑原文件造成误导（v0.5.2）
    } catch (e) { await handleSaveError(e); }
  };
}

// ============ 原始 JSON / 文本编辑 ============

function buildRawArea(initial, { json = false, flex = false } = {}) {
  const root = el(`
    <div class="raw-wrap" ${flex ? 'style="padding-bottom:14px"' : ''}>
      <textarea class="raw-area" spellcheck="false"></textarea>
      <div class="raw-status">
        <span class="info">共 <b class="lines">0</b> 行</span>
        ${json ? '<span class="json-state"></span>' : ''}
        ${json ? '<button class="btn sm" data-fmt>格式化</button>' : ''}
      </div>
    </div>`);
  const area = root.querySelector('.raw-area');
  area.value = initial;

  const linesEl = root.querySelector('.lines');
  const updateLines = () => { linesEl.textContent = area.value.split('\n').length; };
  updateLines();
  area.addEventListener('input', updateLines);

  area.addEventListener('keydown', (e) => {
    if (e.key === 'Tab') {
      e.preventDefault();
      const { selectionStart: s, selectionEnd: t2 } = area;
      area.value = area.value.slice(0, s) + '  ' + area.value.slice(t2);
      area.selectionStart = area.selectionEnd = s + 2;
      area.dispatchEvent(new Event('input'));
    }
  });

  let stateEl = root.querySelector('.json-state');
  const validate = () => {
    if (!json) return true;
    try {
      JSON.parse(area.value);
      stateEl.className = 'json-state ok';
      stateEl.textContent = '✓ JSON 有效';
      return true;
    } catch (err) {
      stateEl.className = 'json-state bad';
      stateEl.textContent = '✗ ' + err.message;
      return false;
    }
  };
  validate();
  if (json) area.addEventListener('input', validate);

  root.querySelector('[data-fmt]')?.addEventListener('click', () => {
    try {
      area.value = JSON.stringify(JSON.parse(area.value), null, 2);
      updateLines();
      validate();
      markDirty();
    } catch { /* 状态栏已提示 */ }
  });

  return { root, area, validate };
}

async function buildRawEditor(item, kind) {
  const isJsonLike = item.fileName.toLowerCase().endsWith('.json');
  const tabs = $('#editor-tabs');
  tabs.hidden = false;
  tabs.innerHTML = `<button data-tab="raw" class="active">原文</button>`;
  const body = $('#editor-body');
  body.innerHTML = '';
  const { content } = await api.text(item.id);
  const raw = buildRawArea(content, { json: isJsonLike, flex: true });
  body.appendChild(raw.root);
  bindDirty(raw.root);

  saveFn = async () => {
    if (isJsonLike && !raw.validate()) {
      toast('JSON 校验失败，请先修正', 'err');
      return;
    }
    try {
      const r = await api.saveText(item.id, raw.area.value, item.modifiedAt);
      clearDirty();
      if (r.warnings?.length) toast(r.warnings.join('；'), 'err');
      item.modifiedAt = r.modifiedAt;
      toast('已保存');
      refreshItems();
      refreshMeta();
    } catch (e) { await handleSaveError(e); } // 409 恢复路径（v0.5.2 N5）
  };

  saveAsFn = async () => {
    if (isJsonLike && !raw.validate()) {
      toast('JSON 校验失败，无法另存', 'err');
      return;
    }
    try {
      const r = await api.saveTextAs(item.id, raw.area.value);
      clearDirty();
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
      closeEditor(); // 另存后关闭，避免继续编辑原文件造成误导（v0.5.2）
    } catch (e) { await handleSaveError(e); }
  };
}
