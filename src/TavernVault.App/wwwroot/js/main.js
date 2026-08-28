// 入口：主题、初始化、设置弹窗

import { api } from './api.js';
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
export function showSettings() {
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
    <div class="m-actions">
      <button class="btn" data-act="rescan"><span class="ico">${icon('refresh')}</span>重新扫描</button>
      <button class="btn primary" data-act="close">完成</button>
    </div>
    <p style="margin-top:12px;font-size:11px;color:var(--text-3)">
      收藏与标签保存在 %APPDATA%\\TavernVault；删除资源只是移入系统回收站。
    </p>`;

  const mask = openModal(body);
  hydrateIcons(body);

  const renderRoots = () => {
    const box = body.querySelector('.root-list');
    box.innerHTML = '';
    (state.meta?.roots || []).forEach((r) => {
      const item = document.createElement('div');
      item.className = 'root-item';
      item.innerHTML = `
        <span class="ico">${icon('folder')}</span>
        <span class="path">${escapeHtml(r)}</span>
        <button class="icon-btn" title="移除"><span class="ico">${icon('trash')}</span></button>`;
      item.querySelector('button').addEventListener('click', async () => {
        const ok = await confirmDialog({
          title: '移除库目录',
          message: `“${r}” 将从管理列表移除（不会删除磁盘上的文件）。`,
          okText: '移除', danger: true,
        });
        if (!ok) return;
        await api.removeRoot(r);
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
  body.addEventListener('click', async (e) => {
    if (e.target.closest('[data-act=rescan]')) {
      await doRescan();
    }
    if (e.target.closest('[data-act=close]') || e.target.closest('[data-act=close-x]')) mask.remove();
  });
}

export async function doRescan() {
  const btn = $('#btn-rescan');
  btn.style.pointerEvents = 'none';
  $('#scan-info').textContent = '正在扫描…';
  try {
    const r = await api.rescan();
    await refreshMeta();
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
    Object.assign(state.filter, { kind: null, tag: null, fav: false, q: '' });
    $('#search').value = '';
    refreshItems();
  });

  try {
    // 启动时自动重扫，保证索引与磁盘一致（小库瞬间完成）
    await api.rescan();
    await refreshMeta();
    updateScanInfo();
    renderSidebar();
    await refreshItems();
  } catch (e) {
    document.getElementById('empty').hidden = false;
    document.getElementById('empty').textContent = `无法连接本地服务：${e.message}`;
  }
}

boot();
