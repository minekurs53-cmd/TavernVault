// preset-model.js 单测（无框架：node tests/preset-model.test.mjs，退出码即结果）
// 覆盖三期写回的保形合同：重排不丢项、增删双数组一致、系统项拒删、未知字段保留。
import assert from 'node:assert/strict';
import { addPrompt, getGroups, isSystemPrompt, pickGroup, removePrompt, reorderGroup }
  from '../src/TavernVault.App/wwwroot/js/preset-model.js';

let passed = 0;
function check(name, fn) {
  try { fn(); passed++; console.log(`  PASS ${name}`); }
  catch (e) { console.error(`  FAIL ${name}\n${e.message}`); process.exitCode = 1; }
}

// 官方风格夹具：prompt_order 两组（100001 默认 + 100052 第二角色），
// prompt 带非标准扩展字段（extension_field）验证保形
function fixture() {
  return {
    temperature: 1,
    prompts: [
      { identifier: 'main', name: '主提示词', system_prompt: true, marker: true, extension_field: 'keep' },
      { identifier: 'chatHistory', name: '聊天历史', system_prompt: true, marker: true },
      { identifier: 'charDescription', name: '角色描述', system_prompt: false, role: 'system', content: '描述' },
      { identifier: 'custom-a', name: '自定义A', role: 'system', content: 'AAA', custom: { nested: 1 } },
      { identifier: 'custom-b', name: '自定义B', role: 'user', content: 'BBB' },
    ],
    prompt_order: [
      {
        character_id: 100001,
        order: [
          { identifier: 'main', enabled: true },
          { identifier: 'charDescription', enabled: true },
          { identifier: 'custom-a', enabled: false },
          { identifier: 'custom-b', enabled: true },
        ],
      },
      { character_id: 100052, order: [{ identifier: 'main', enabled: true }, { identifier: 'custom-b', enabled: true }] },
    ],
    unknown_top: 'preserve-me',
  };
}

console.log('== pickGroup / getGroups ==');
check('默认选 100001', () => {
  const p = fixture();
  assert.equal(pickGroup(p, undefined).character_id, 100001);
});
check('按 characterId 精确选择第二组', () => {
  const p = fixture();
  assert.equal(pickGroup(p, 100052).character_id, 100052);
});
check('未知 characterId 回退默认组', () => {
  const p = fixture();
  assert.equal(pickGroup(p, 999999).character_id, 100001);
});
check('非法 prompt_order 元素被规整掉', () => {
  const p = { prompt_order: [{ character_id: 1, order: [] }, null, { character_id: 2 }, 42] };
  assert.equal(getGroups(p).length, 1);
  assert.equal(pickGroup(p).character_id, 1);
});
check('无分组返回 null', () => {
  assert.equal(pickGroup({ prompts: [] }), null);
  assert.equal(pickGroup({ prompt_order: 'bad' }), null);
});

console.log('== reorderGroup（拖拽排序写回）==');
check('重排只动次序，enabled 与对象引用保持', () => {
  const p = fixture();
  const g = pickGroup(p, 100001);
  const [main, desc, a, b] = g.order;
  const reordered = reorderGroup(p, 100001, [a, b, main, desc]);
  assert.deepEqual(reordered.map((o) => o.identifier), ['custom-a', 'custom-b', 'main', 'charDescription']);
  assert.equal(reordered[0], a); // 原对象引用（enabled 等字段自然保留）
  assert.equal(reordered[0].enabled, false);
});
check('UI 漏传的项按原次序补回末尾（不静默丢条目）', () => {
  const p = fixture();
  const g = pickGroup(p, 100001);
  const a = g.order[2], b = g.order[3];
  const reordered = reorderGroup(p, 100001, [a, b]); // 漏了 main/charDescription
  assert.deepEqual(reordered.map((o) => o.identifier),
    ['custom-a', 'custom-b', 'main', 'charDescription']);
});
check('重排不触碰 prompts[] 与顶层未知字段', () => {
  const p = fixture();
  reorderGroup(p, 100001, [...pickGroup(p, 100001).order].reverse());
  assert.equal(p.prompts.length, 5);
  assert.equal(p.unknown_top, 'preserve-me');
  assert.equal(p.prompts[0].extension_field, 'keep');
});
check('重排只作用于选定分组', () => {
  const p = fixture();
  reorderGroup(p, 100001, [...pickGroup(p, 100001).order].reverse());
  assert.deepEqual(pickGroup(p, 100052).order.map((o) => o.identifier), ['main', 'custom-b']);
});

console.log('== addPrompt（新增提示词）==');
check('写入 prompts 末尾 + 当前分组 order 末尾 enabled:true', () => {
  const p = fixture();
  const created = addPrompt(p, 100001, { name: '新条目', role: 'user', content: '内容' }, () => 'new-1');
  assert.equal(p.prompts.length, 6);
  assert.equal(created.identifier, 'new-1');
  assert.deepEqual(pickGroup(p, 100001).order.at(-1), { identifier: 'new-1', enabled: true });
  assert.equal(pickGroup(p, 100052).order.length, 2); // 其他分组不受影响
});
check('identifier 冲突时重新生成', () => {
  const p = fixture();
  let n = 0;
  const gen = () => (n++ === 0 ? 'custom-a' : 'new-2'); // 第一次撞现有 id
  const created = addPrompt(p, 100001, { name: 'x' }, gen);
  assert.equal(created.identifier, 'new-2');
});
check('显式 identifier（测试注入）与默认字段', () => {
  const p = fixture();
  const created = addPrompt(p, 100001, { identifier: 'fixed-id' });
  assert.equal(created.identifier, 'fixed-id');
  assert.equal(created.name, '新提示词');
  assert.equal(created.role, 'system');
  assert.equal(created.content, '');
});
check('prompts 缺失（非法文件）返回 null 不抛异常', () => {
  assert.equal(addPrompt({}, 100001, {}), null);
});

console.log('== removePrompt（删除提示词）==');
check('从 prompts 与所有分组 order 同步移除', () => {
  const p = fixture();
  const r = removePrompt(p, 'custom-b');
  assert.equal(r.ok, true);
  assert.equal(r.fromOrders, 2); // 两组里都在
  assert.equal(p.prompts.length, 4);
  assert.equal(pickGroup(p, 100001).order.length, 3);
  assert.equal(pickGroup(p, 100052).order.length, 1);
});
check('仅在单分组出现时 fromOrders=1', () => {
  const p = fixture();
  const r = removePrompt(p, 'custom-a');
  assert.equal(r.ok, true);
  assert.equal(r.fromOrders, 1);
});
check('系统管理项拒删', () => {
  const p = fixture();
  assert.deepEqual(removePrompt(p, 'main'), { ok: false, reason: 'system' });
  assert.deepEqual(removePrompt(p, 'chatHistory'), { ok: false, reason: 'system' });
  assert.equal(p.prompts.length, 5); // 未动
});
check('不存在的 identifier 返回 notfound', () => {
  assert.deepEqual(removePrompt(fixture(), 'nope'), { ok: false, reason: 'notfound' });
});
check('自定义项可删（isSystemPrompt=false 的判定边界）', () => {
  assert.equal(isSystemPrompt({ marker: false, system_prompt: false }), false);
  assert.equal(isSystemPrompt({ marker: true }), true);
  assert.equal(isSystemPrompt({ system_prompt: true }), true);
});

console.log(`\n结果：${passed} 通过，${process.exitCode ? '存在失败' : '0 失败'}`);
if (process.exitCode) process.exit(1);
