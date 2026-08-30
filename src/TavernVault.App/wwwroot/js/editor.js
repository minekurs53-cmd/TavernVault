// 编辑器：角色卡（表单 / 原始JSON）、世界书（条目）、预设/美化/脚本/文本（原始JSON）

import { api } from './api.js';
import { el, $, icon, hydrateIcons, toast, escapeHtml, confirmDialog } from './util.js';
import { refreshItems, refreshMeta, kindMeta } from './app.js';

let dirty = false;
let onCloseCleanup = null;

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
  const overlay = document.getElementById('editor-overlay');
  overlay.hidden = false;
  overlay.innerHTML = `
    <div class="editor-head">
      <button class="icon-btn editor-close" title="关闭 (Esc)"><span class="ico">${icon('x')}</span></button>
      <h2>${title}</h2>
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
    body.innerHTML = '';
    body.appendChild(el(`<div class="empty">加载失败：${escapeHtml(e.message)}</div>`));
  }
}

export async function closeEditor() {
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
}

let saveFn = null;
let saveAsFn = null;
function doSave() { saveFn?.(); }
function doSaveAs() {
  if (!saveAsFn) { toast('当前视图不支持另存为', 'err'); return; }
  saveAsFn?.();
}

function bindDirty(root) {
  root.addEventListener('input', () => markDirty());
  root.addEventListener('change', () => markDirty());
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
  const data = preloaded ? { card: preloaded } : await api.card(item.id);
  let card = data.card; // 完整卡片 JSON
  let dataNode = card.data || card;

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
      const gaBox = el(`<div class="field"><label>备用开场白（${(dataNode.alternate_greetings || []).length}）</label><div class="greetings"></div>
        <button class="btn sm" data-add-greeting>＋ 添加备用开场白</button></div>`);
      const list = gaBox.querySelector('.greetings');
      const renderGreetings = () => {
        list.innerHTML = '';
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

  tabs.addEventListener('click', (e) => {
    const tab = e.target.closest('[data-tab]')?.dataset.tab;
    if (!tab) return;
    tabs.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === e.target));
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
        // 原始视图 → 表单：解析最新 JSON 并重建表单
        try { card = JSON.parse(raw.area.value); } catch { /* 状态栏已提示 */ }
        dataNode = card.data || card;
        clearDirty();
        buildCharacterEditor(item, card);
        return;
      }
      formView.hidden = false;
      rawView.hidden = true;
    }
  });

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
      const r = await api.saveCard(item.id, payload);
      clearDirty();
      toast('已保存到 ' + (item.hasEmbeddedCard ? 'PNG 内嵌数据' : 'JSON 文件'));
      refreshItems();
      refreshMeta();
      // 重新加载以拿到服务端合并后的最新数据
      const fresh = await api.card(r.id || item.id);
      card = fresh.card;
    } catch (e) {
      if (e instanceof SyntaxError) toast('JSON 格式错误：' + e.message, 'err');
      else toast(e.message, 'err');
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
    } catch (e) {
      if (e instanceof SyntaxError) toast('JSON 格式错误：' + e.message, 'err');
      else toast(e.message, 'err');
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
  let entries = data.entries.map((e) => ({ key: e.key, data: e.data || {}, raw: e.raw }));
  let selected = 0;

  const body = $('#editor-body');
  body.innerHTML = `
    <div class="lore-layout">
      <div class="lore-list">
        <div class="lore-list-head">
          <div class="row1">
            <span class="count"><b class="lore-count">${entries.length}</b> 个条目</span>
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
  searchInput.addEventListener('input', renderList);
  bindDirty(body);

  renderList();
  renderForm();

  saveFn = async () => {
    try {
      if (embedded) {
        // raw（Spec 原条目）原样回传，服务端只合并被编辑的字段
        const r = await api.saveCardBook(item.id, {
          entries: entries.map((e) => ({ key: e.key, data: e.data, raw: e.raw })),
        });
        clearDirty();
        toast(`内置世界书已保存（${r.count} 个条目）`);
        refreshItems();
        refreshMeta();
      } else {
        const r = await api.saveLore(item.id, {
          entries: entries.map((e) => ({ key: e.key, data: e.data })),
        });
        clearDirty();
        toast(`已保存（${r.count} 个条目）`);
        refreshItems();
        refreshMeta();
      }
    } catch (e) { toast(e.message, 'err'); }
  };

  saveAsFn = async () => {
    try {
      const payload = entries.map((e) => ({ key: e.key, data: e.data, raw: e.raw }));
      const r = embedded
        ? await api.saveCardBookAs(item.id, payload) // 导出为独立世界书
        : await api.saveLoreAs(item.id, payload);
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
    } catch (e) { toast(e.message, 'err'); }
  };
}

// ============ 预设可视化（B 计划一期：只读） ============

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
  continue_nudge: '续写推动', bias_preset_selected: '偏好预设',
  function_calling: '函数调用', show_thoughts: '显示思考',
  reasoning_effort: '推理力度', enable_web_search: '联网搜索',
  request_images: '请求图片', image_inlining: '图片内联',
  assistant_prefill: '助手预填充', assistant_impersonation: '助手扮演预填充',
  claude_use_sysprompt: 'Claude 系统提示', prompt_instructions: '指令提示',
  prompt_order: '生效顺序', prompts: '提示词列表',
  utility_prompts: '工具提示词', regex: '正则',
};

const ROLE_LABELS = { system: '系统', user: '用户', assistant: 'AI' };

// 预设可视化视图：采样参数总览 + 按 prompt_order 解析的生效顺序 + 内容查看
async function buildPresetEditor(item) {
  const { content } = await api.text(item.id);
  let preset;
  try { preset = JSON.parse(content); } catch { preset = null; }

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

  // ---- 原文视图 ----
  const raw = buildRawArea(content, { json: true, flex: true });
  rawView.appendChild(raw.root);
  bindDirty(rawView);

  // ---- 可视化视图（只读）----
  if (preset) renderPresetVisual(visualView, preset);
  else visualView.appendChild(el('<div class="empty">JSON 解析失败，请使用原文视图修正</div>'));

  let visualActive = true;
  tabs.addEventListener('click', (e) => {
    const tab = e.target.closest('[data-tab]')?.dataset.tab;
    if (!tab) return;
    tabs.querySelectorAll('button').forEach((b) => b.classList.toggle('active', b === e.target));
    visualActive = tab === 'visual';
    visualView.hidden = !visualActive;
    rawView.hidden = visualActive;
    if (!visualActive && dirty) { clearDirty(); raw.area.value = content; } // 可视化不改内容
  });

  saveFn = async () => {
    if (visualActive) { toast('可视化视图为只读，请切换到"原文"修改后保存', 'err'); return; }
    if (!raw.validate()) { toast('JSON 校验失败，请先修正', 'err'); return; }
    try {
      await api.saveText(item.id, raw.area.value);
      clearDirty();
      toast('已保存');
      refreshItems();
      refreshMeta();
      const fresh = await api.text(item.id);
      content = fresh.content;
      try { preset = JSON.parse(content); visualView.innerHTML = ''; renderPresetVisual(visualView, preset); } catch { }
    } catch (e) { toast(e.message, 'err'); }
  };

  saveAsFn = async () => {
    if (visualActive) { toast('可视化视图为只读，请切换到"原文"后再另存', 'err'); return; }
    if (!raw.validate()) { toast('JSON 校验失败，无法另存', 'err'); return; }
    try {
      const r = await api.saveTextAs(item.id, raw.area.value);
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
    } catch (e) { toast(e.message, 'err'); }
  };
}

function renderPresetVisual(root, preset) {
  const prompts = Array.isArray(preset.prompts) ? preset.prompts : [];
  const orderGroups = Array.isArray(preset.prompt_order) ? preset.prompt_order : [];
  const group = orderGroups.find((g) => g.character_id === 100001) || orderGroups[0];
  const orderList = Array.isArray(group?.order) ? group.order : [];
  const byId = new Map(prompts.map((p) => [p.identifier, p]));
  const orderedIds = new Set(orderList.map((o) => o.identifier));

  const wrap = el('<div class="form-wrap"></div>');

  // --- 采样参数总览 ---
  const paramsCard = el('<div class="form-card"><h4>采样与生成参数（只读）</h4><div class="param-grid"></div></div>');
  const grid = paramsCard.querySelector('.param-grid');
  const entries = Object.entries(preset).filter(([k, v]) =>
    (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean')
    && !k.startsWith('_') && v !== '');
  for (const [k, v] of entries) {
    const label = SAMPLER_LABELS[k] || k;
    grid.appendChild(el(`<div class="param" title="${escapeHtml(k)}">
      <span>${escapeHtml(label)}</span><b>${escapeHtml(String(v))}</b></div>`));
  }
  if (!entries.length) grid.appendChild(el('<div class="empty" style="padding:10px">无标量参数</div>'));
  wrap.appendChild(paramsCard);

  // --- 生效顺序 ---
  const orderCard = el(`<div class="form-card"><h4>生效顺序（prompt_order · 默认角色）</h4>
    <div class="preset-list"></div></div>`);
  const list = orderCard.querySelector('.preset-list');
  let enabledCount = 0;
  orderList.forEach((o, idx) => {
    const p = byId.get(o.identifier);
    const isMarker = p?.marker === true || p?.system_prompt === true;
    const name = p?.name || o.identifier;
    const role = isMarker ? '系统' : (p?.role ? (ROLE_LABELS[p.role] || p.role) : '—');
    const len = p?.content ? p.content.length : 0;
    if (o.enable) enabledCount++;
    const row = el(`<div class="preset-row ${o.enable ? '' : 'off'}" data-ident="${escapeHtml(o.identifier)}">
      <span class="po-idx">${idx + 1}</span>
      <span class="po-en">${o.enable ? '✓' : '✗'}</span>
      <span class="po-name">${escapeHtml(name)}</span>
      <span class="po-role">${role}</span>
      <span class="po-len">${len ? len + ' 字' : ''}</span>
    </div>`);
    row.addEventListener('click', () => {
      list.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('sel'));
      row.classList.add('sel');
      renderPresetDetail(detail, p, o.identifier);
    });
    list.appendChild(row);
  });
  wrap.appendChild(orderCard);

  // 未加入生效序列的提示词
  const unordered = prompts.filter((p) => !orderedIds.has(p.identifier));
  if (unordered.length) {
    const uCard = el('<div class="form-card"><h4>未加入生效序列的提示词</h4><div class="preset-list"></div></div>');
    const ul = uCard.querySelector('.preset-list');
    unordered.forEach((p) => {
      const row = el(`<div class="preset-row" data-ident="${escapeHtml(p.identifier)}">
        <span class="po-idx">·</span><span class="po-en"></span>
        <span class="po-name">${escapeHtml(p.name || p.identifier)}</span>
        <span class="po-role">${p.marker === true || p.system_prompt === true ? '系统' : (ROLE_LABELS[p.role] || p.role || '—')}</span>
        <span class="po-len">${p.content ? p.content.length + ' 字' : ''}</span>
      </div>`);
      row.addEventListener('click', () => {
        ul.querySelectorAll('.preset-row').forEach((r) => r.classList.remove('sel'));
        row.classList.add('sel');
        renderPresetDetail(detail, p, p.identifier);
      });
      ul.appendChild(row);
    });
    wrap.appendChild(uCard);
  }

  // --- 内容详情 ---
  const detailCard = el('<div class="form-card"><h4>提示词内容（点击上方条目查看）</h4><div class="preset-detail"><div class="empty" style="padding:14px">尚未选择</div></div></div>');
  const detail = detailCard.querySelector('.preset-detail');
  wrap.appendChild(detailCard);

  // 汇总
  const sumCard = el(`<div class="form-card"><h4>统计</h4><div class="param-grid">
    <div class="param"><span>提示词总数</span><b>${prompts.length}</b></div>
    <div class="param"><span>生效序列</span><b>${orderList.length}（启用 ${enabledCount}）</b></div>
    <div class="param"><span>未排序</span><b>${unordered.length}</b></div>
    <div class="param"><span>排序组</span><b>${orderGroups.length}</b></div>
  </div></div>`);
  wrap.appendChild(sumCard);

  root.appendChild(wrap);
}

function renderPresetDetail(box, p, identifier) {
  if (!p) {
    box.innerHTML = `<div class="empty" style="padding:14px">提示词 "${escapeHtml(identifier)}" 在 prompts 列表中不存在（可能已被删除）</div>`;
    return;
  }
  const isMarker = p.marker === true || p.system_prompt === true;
  const meta = [];
  if (p.role) meta.push('角色：' + (ROLE_LABELS[p.role] || p.role));
  if (isMarker) meta.push('系统管理项');
  if (p.injection_position === 1) meta.push('按深度注入 @ ' + (p.injection_depth ?? 0));
  if (p.forbid_overrides) meta.push('禁止覆盖');
  box.innerHTML = `
    <div class="pd-head"><b>${escapeHtml(p.name || identifier)}</b>
      <span class="chip accent">${escapeHtml(identifier)}</span></div>
    <div class="pd-meta">${meta.map(escapeHtml).join(' · ') || '—'}</div>
    <pre class="pd-content">${escapeHtml(p.content || '（空）')}</pre>`;
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
      await api.saveText(item.id, raw.area.value);
      clearDirty();
      toast('已保存');
      refreshItems();
      refreshMeta();
    } catch (e) { toast(e.message, 'err'); }
  };

  saveAsFn = async () => {
    if (isJsonLike && !raw.validate()) {
      toast('JSON 校验失败，无法另存', 'err');
      return;
    }
    try {
      const r = await api.saveTextAs(item.id, raw.area.value);
      toast(`已另存为 ${r.fileName}`);
      refreshItems();
      refreshMeta();
    } catch (e) { toast(e.message, 'err'); }
  };
}
