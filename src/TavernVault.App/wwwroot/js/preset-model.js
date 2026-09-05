// 预设可视化 · 数据操作纯函数（v0.7.0 三期）
// 无 DOM 依赖：editor.js 负责界面与 dirty/保存链，本模块只做 prompt_order / prompts 的
// 结构化写回，保证"未知字段不丢、prompts 与 order 一致"。Node 可直接 import 做单测
// （tests/preset-model.test.mjs；wwwroot/js/package.json 声明 ESM 仅供 Node 识别，浏览器无感）。

export const DEFAULT_CHARACTER_ID = 100001;

/** 系统管理项：marker 或 system_prompt 为 true——内容只读、禁止删除（防误删 main/jailbreak 等） */
export function isSystemPrompt(p) {
  return p?.marker === true || p?.system_prompt === true;
}

/** 规整后的分组列表（剔除无 order 数组的非法元素） */
export function getGroups(preset) {
  const arr = Array.isArray(preset?.prompt_order) ? preset.prompt_order : [];
  return arr.filter((g) => g && typeof g === 'object' && Array.isArray(g.order));
}

/**
 * 选定分组：characterId 精确匹配 → 默认 100001 → 第一组；无任何分组返回 null。
 * 调用方拿到 null 时按"无生效序列"渲染（与旧行为一致）。
 */
export function pickGroup(preset, characterId) {
  const groups = getGroups(preset);
  if (!groups.length) return null;
  return groups.find((g) => g.character_id === characterId)
    || groups.find((g) => g.character_id === DEFAULT_CHARACTER_ID)
    || groups[0];
}

/**
 * 重排指定分组的 order：newItems 是新次序的 order 项（沿用原对象引用，enabled 等字段不动）。
 * 只重排数组本身，不触碰 prompts[]。防御：UI 传丢的项按原次序补回末尾，永不静默丢条目。
 * 返回重排后的数组（即 group.order）。
 */
export function reorderGroup(preset, characterId, newItems) {
  const group = pickGroup(preset, characterId);
  if (!group) return [];
  const valid = newItems.filter((o) => o && typeof o === 'object');
  if (valid.length < group.order.length) {
    const seen = new Set(valid.map((o) => o.identifier));
    for (const o of group.order) {
      if (!seen.has(o.identifier)) valid.push(o); // UI 漏传的项补回
    }
  }
  group.order.length = 0;
  group.order.push(...valid);
  return group.order;
}

/**
 * 新增提示词：写入 prompts[] 末尾 + 追加到指定分组 order 末尾（enabled:true）。
 * fields.identifier 可显式传入（测试用）；否则自动生成并保证不与现有 identifier 冲突。
 * 返回新建的 prompt 对象；preset 结构非法（无 prompts 数组）时返回 null。
 */
export function addPrompt(preset, characterId, fields = {}, genId = defaultGenerateId) {
  if (!Array.isArray(preset?.prompts)) return null;
  const existing = new Set(preset.prompts.map((p) => p?.identifier));
  let identifier = fields.identifier ?? genId();
  while (existing.has(identifier)) identifier = genId();

  const prompt = {
    name: fields.name || '新提示词',
    system_prompt: false,
    role: fields.role || 'system',
    content: fields.content || '',
    identifier,
  };
  preset.prompts.push(prompt);

  const group = pickGroup(preset, characterId);
  if (group) group.order.push({ identifier, enabled: true });
  return prompt;
}

/**
 * 删除提示词：从 prompts[] 与所有分组 order[] 同步移除。
 * 返回 { ok:true, fromOrders }；系统管理项返回 { ok:false, reason:'system' }；
 * 找不到返回 { ok:false, reason:'notfound' }。
 */
export function removePrompt(preset, identifier) {
  if (!Array.isArray(preset?.prompts)) return { ok: false, reason: 'notfound' };
  const idx = preset.prompts.findIndex((p) => p?.identifier === identifier);
  if (idx === -1) return { ok: false, reason: 'notfound' };
  if (isSystemPrompt(preset.prompts[idx])) return { ok: false, reason: 'system' };

  preset.prompts.splice(idx, 1);
  let fromOrders = 0;
  for (const g of getGroups(preset)) {
    const oi = g.order.findIndex((o) => o?.identifier === identifier);
    if (oi !== -1) { g.order.splice(oi, 1); fromOrders++; }
  }
  return { ok: true, fromOrders };
}

function defaultGenerateId() {
  return (globalThis.crypto?.randomUUID?.() ?? `new-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`);
}
