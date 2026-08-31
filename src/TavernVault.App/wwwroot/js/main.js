// 入口：主题、初始化、设置弹窗

import { api, get, post } from './api.js';
import { $, icon, hydrateIcons, toast, escapeHtml, openModal, confirmDialog } from './util.js';
import { state, initShell, refreshItems, refreshMeta, renderSidebar } from './app.js';

// ---- 主题 ----
function initTheme() {
  const saved = localStorage.getItem('tv-theme');
  const dark = saved
    ? saved === 'dark'
    : window.matchMedia('(prefers-color-scheme: dark)').matches;
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  $('#btn-theme .ico').innerHTML = icon(dark ? 'sun' : 'moon');
}

// ---- 设置弹窗 ----
export async function showSettings() {
  const roots = state.meta?.roots || [];
  const body = document.createElement('div');
  body.innerHTML = `
    <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
      <h3 style="margin:0">库设置</h3>
      <button class="icon-btn" data-act="close-x" title="关闭"><span class="ico">${icon('x')}</span></button>
    </div>
    <p>纳入管理的文件夹会被递归扫描并自动识别类型；原始文件不会被移动或修改（除非你主动编辑）。</p>
    <div class="m-section-title">库根目录</div>
    <div class="root-list"></div>
    <div class="add-root-row">
      <input type="text" id="set-root-input" placeholder="输入文件夹路径，或点右侧选择">
      <button class="btn" id="set-root-pick"><span class="ico">${icon('folder')}</span>浏览…</button>
      <button class="btn primary" id="set-root-add">添加</button>
    </div>
    <div class="add-root-row" style="margin-top:6px">
      <button class="btn" id="set-tavern-connect" style="width:100%">接入酒馆（自动检测 SillyTavern / TauriTavern）</button>
    </div>
    <div class="m-section-title">备份</div>
    <div class="toggle-row">
      <label class="toggle"><input type="checkbox" id="set-autobackup"> 编辑/还原前自动备份原文件</label>
    </div>
    <div class="field" style="max-width:220px"><label>每个文件保留备份份数</label>
      <input type="text" id="set-maxbackups" inputmode="numeric"></div>
    <div class="field"><label>备份存储位置（留空 = 默认位置）</label>
      <div class="add-root-row">
        <input type="text" id="set-backup-dir" placeholder="默认位置">
        <button class="btn" id="set-backup-pick"><span class="ico">${icon('folder')}</span>浏览…</button>
      </div>
    </div>
    <div class="m-actions">
      <button class="btn" data-act="rescan"><span class="ico">${icon('refresh')}</span>重新扫描</button>
      <button class="btn primary" data-act="close">完成</button>
    </div>
    <p style="margin-top:12px;font-size:11px;color:var(--text-3)">
      收藏与标签保存在 %APPDATA%\\TavernVault；删除资源只是移入系统回收站。
    </p>`;

  const mask = openModal(body);
  hydrateIcons(body);

  // 备份设置回显与保存
  const stats = await get('/api/backups/stats').catch(() => null);
  const autoCb = body.querySelector('#set-autobackup');
  const maxInput = body.querySelector('#set-maxbackups');
  const dirInput = body.querySelector('#set-backup-dir');
  let curDir = '';
  if (stats) {
    autoCb.checked = !!stats.autoBackup;
    maxInput.value = stats.maxPerFile;
    curDir = stats.dir || '';
    dirInput.value = curDir;
    dirInput.placeholder = stats.defaultDir ? `默认：${stats.defaultDir}` : '默认位置';
  }
  const saveBackupSettings = async () => {
    const max = Math.min(50, Math.max(1, parseInt(maxInput.value, 10) || 5));
    maxInput.value = max;
    await post('/api/settings/backup', { autoBackup: autoCb.checked, maxPerFile: max }).catch(() => {});
  };
  autoCb.addEventListener('change', saveBackupSettings);
  maxInput.addEventListener('change', saveBackupSettings);

  dirInput.addEventListener('change', async () => {
    const v = dirInput.value.trim();
    if (v === curDir) return;
    const ok = await confirmDialog({
      title: '更改备份位置',
      message: v
        ? `现有备份会一并移动到新位置：${v}。继续？`
        : '恢复默认备份位置？现有备份会移动回去。',
      okText: '移动并切换',
    });
    if (!ok) { dirInput.value = curDir; return; }
    try {
      const r = await post('/api/settings/backup', { backupDir: v });
      curDir = r.dir || '';
      dirInput.value = curDir;
      toast('备份位置已更新');
    } catch (e) {
      dirInput.value = curDir;
      toast(e.message, 'err');
    }
  });
  body.querySelector('#set-backup-pick').addEventListener('click', async () => {
    try {
      const r = await api.pickFolder();
      if (r?.path) {
        dirInput.value = r.path;
        dirInput.dispatchEvent(new Event('change'));
      }
    } catch (e) { toast(e.message, 'err'); }
  });

  const renderRoots = () => {
    const box = body.querySelector('.root-list');
    box.innerHTML = '';
    (state.meta?.roots || []).forEach((r) => {
      const item = document.createElement('div');
      item.className = 'root-item' + (r.source !== 'normal' ? ' tavern' : '');
      const badge = r.source === 'tavernST' ? '<span class="root-badge tavernST">ST</span>'
        : r.source === 'tavernTT' ? '<span class="root-badge tavernTT">TT</span>' : '';
      item.innerHTML = `
        <span class="ico">${icon('folder')}</span>
        <span class="path">${escapeHtml(r.path)}</span>
        ${badge}
        <button class="icon-btn" title="移除"><span class="ico">${icon('trash')}</span></button>`;
      item.querySelector('button').addEventListener('click', async () => {
        const ok = await confirmDialog({
          title: '移除库目录',
          message: `“${r.path}” 将从管理列表移除（不会删除磁盘上的文件）。`,
          okText: '移除', danger: true,
        });
        if (!ok) return;
        await api.removeRoot(r.path);
        await refreshMeta();
        renderRoots();
        renderSidebar();
        refreshItems();
      });
      box.appendChild(item);
    });
  };
  renderRoots();

  const input = body.querySelector('#set-root-input');
  body.querySelector('#set-root-add').addEventListener('click', async () => {
    const path = input.value.trim();
    if (!path) return;
    try {
      await api.addRoot(path);
      input.value = '';
      await refreshMeta();
      renderRoots();
      renderSidebar();
      refreshItems();
      toast('已添加并扫描');
    } catch (e) { toast(e.message, 'err'); }
  });
  body.querySelector('#set-root-pick').addEventListener('click', async () => {
    try {
      const r = await api.pickFolder();
      if (r?.path) input.value = r.path;
    } catch (e) { toast(e.message, 'err'); }
  });
  body.querySelector('#set-tavern-connect').addEventListener('click', () =>
    showTavernWizard(async () => {
      await refreshMeta();
      renderRoots();
      renderSidebar();
      refreshItems();
    }));
  body.addEventListener('click', async (e) => {
    if (e.target.closest('[data-act=rescan]')) {
      await doRescan();
    }
    if (e.target.closest('[data-act=close]') || e.target.closest('[data-act=close-x]')) mask.remove();
  });
}

// ---- 接入酒馆向导：检测 → 选择 → 注册 ----
async function showTavernWizard(onConnected) {
  const body = document.createElement('div');
  body.innerHTML = `
    <h3>接入酒馆</h3>
    <p>检测本机的酒馆安装，把其资源目录（角色卡、世界书、预设等）纳入统一管理。接入后这些文件的重命名/移动会先询问确认。</p>
    <div class="tw-list"><div class="empty" style="padding:12px">正在检测…</div></div>
    <div class="m-actions">
      <button class="btn" data-act="close">关闭</button>
    </div>`;
  const mask = openModal(body);
  const list = body.querySelector('.tw-list');
  body.addEventListener('click', (e) => {
    if (e.target.closest('[data-act=close]')) mask.remove();
  });

  let found = [];
  try {
    found = (await api.detectTavern()).found || [];
  } catch (e) {
    list.innerHTML = `<div class="tw-warn">检测失败：${escapeHtml(e.message)}</div>`;
    return;
  }
  if (!found.length) {
    list.innerHTML = '<div class="tw-warn">未检测到 SillyTavern 或 TauriTavern 安装。</div>';
    return;
  }

  list.innerHTML = found.map((f) => `
    <div class="tw-row">
      <div class="tw-name"><span class="root-badge ${f.source}">${f.source === 'tavernST' ? 'ST' : 'TT'}</span> ${escapeHtml(f.label)}</div>
      <div class="tw-sub">${escapeHtml(f.subdirs.join('、'))}</div>
      <button class="btn primary sm" data-src="${f.source}">接入</button>
    </div>`).join('');

  list.addEventListener('click', async (e) => {
    const btn = e.target.closest('[data-src]');
    if (!btn) return;
    btn.disabled = true;
    try {
      const r = await api.connectTavern(btn.dataset.src);
      toast(`已接入，新增 ${r.added} 个库根`);
      mask.remove();
      if (onConnected) await onConnected();
    } catch (err) {
      toast(err.message, 'err');
      btn.disabled = false;
    }
  });
}

function updateVersion() {
  const v = state.meta?.version;
  $('#app-version').textContent = v ? 'v' + v : '';
}

export async function doRescan() {
  const btn = $('#btn-rescan');
  btn.style.pointerEvents = 'none';
  $('#scan-info').textContent = '正在扫描…';
  try {
    const r = await api.rescan();
    await refreshMeta();
    updateVersion();
    await refreshItems();
    toast(`扫描完成，共 ${r.count} 个资源`);
  } catch (e) {
    toast(e.message, 'err');
  } finally {
    btn.style.pointerEvents = '';
    updateScanInfo();
  }
}

function updateScanInfo() {
  const t = state.meta?.lastScanAt;
  if (!t || new Date(t).getFullYear() < 2000) {
    $('#scan-info').textContent = '尚未扫描';
    return;
  }
  $('#scan-info').textContent = `上次扫描：${new Date(t).toLocaleTimeString()}`;
}

// ---- 启动 ----
async function boot() {
  initTheme();
  hydrateIcons();
  initShell();

  $('#btn-rescan').addEventListener('click', doRescan);
  $('#btn-settings').addEventListener('click', showSettings);
  $('#filter-clear').addEventListener('click', () => {
    Object.assign(state.filter, { kind: null, tag: null, fav: false, q: '', dir: null, root: null });
    $('#search').value = '';
    refreshItems();
  });

  try {
    // 启动时自动重扫，保证索引与磁盘一致（小库瞬间完成）
    await api.rescan();
    await refreshMeta();
    // 恢复上次所在库（校验合法性，非法立即回写）
    const LIB_KEYS = ['normal', 'tavernST', 'tavernTT'];
    const saved = localStorage.getItem('tv-library');
    if (LIB_KEYS.includes(saved)) {
      state.filter.library = saved;
    } else {
      localStorage.setItem('tv-library', 'normal');
      state.filter.library = 'normal';
    }
    updateVersion();
    updateScanInfo();
    renderSidebar();
    await refreshItems();
  } catch (e) {
    document.getElementById('empty').hidden = false;
    document.getElementById('empty').textContent = `无法连接本地服务：${e.message}`;
  }
}

boot();
