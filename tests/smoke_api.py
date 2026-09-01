# -*- coding: utf-8 -*-
"""针对本地服务的 API 冒烟测试（写入操作只作用于 testdata 临时库根）。"""
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

BASE = "http://127.0.0.1:47999"
ok_count = 0
fail_count = 0


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method)
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data) as resp:
            raw = resp.read()
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as ex:
        raw = ex.read()
        try:
            return json.loads(raw)
        except Exception:
            return {"error": f"HTTP {ex.code}"}


def check(name, cond, extra=""):
    global ok_count, fail_count
    if cond:
        ok_count += 1
        print(f"  PASS {name} {extra}")
    else:
        fail_count += 1
        print(f"  FAIL {name} {extra}")


# ---- 夹具：创建 testdata 临时库根并注册（脚本自足，不依赖手动准备） ----
import os

TESTDATA = os.path.abspath("testdata")
os.makedirs(TESTDATA, exist_ok=True)
with open(os.path.join(TESTDATA, "测试卡.json"), "w", encoding="utf-8") as f:
    json.dump({
        "spec": "chara_card_v2",
        "avatar": "none", "create_date": "2025-1-1",  # 未知键：验证无损编辑
        "data": {
            "name": "测试卡", "description": "原始描述", "personality": "冷静",
            "character_book": {  # Spec V2 内嵌书：验证 V2→ST 映射、raw 保形合并
                "entries": [{"keys": ["内置词"], "content": "内容", "comment": "内嵌条目",
                             "enabled": True, "insertion_order": 50, "position": "before_char",
                             "id": 42, "selective": True, "extensions": {}}],
            },
        },
    }, f, ensure_ascii=False)
with open(os.path.join(TESTDATA, "测试书.json"), "w", encoding="utf-8") as f:
    json.dump({
        "entries": {"0": {"key": ["词"], "content": "内容", "comment": "条目一",
                          "constant": False, "disable": False, "order": 1,
                          "position": 0, "depth": 4, "probability": 100}},
    }, f, ensure_ascii=False)
call("POST", "/api/roots", {"path": TESTDATA})

print("== 预扫描 ==")
r = call("POST", "/api/rescan")
check("重扫成功", r is not None and r.get("count", 0) > 0, str(r))

print("== 搜索 ==")
items = call("GET", "/api/items?q=" + urllib.parse.quote("测试卡"))
check("搜索测试卡", len(items) == 1, str([i["fileName"] for i in items]))
card_item = items[0]
check("识别为角色卡", card_item["kind"] == "character")

print("== 角色卡字段编辑 ==")
call("PUT", f"/api/cards/{card_item['id']}", {
    "fields": {"description": "修改后的描述", "personality": "活泼"}})
card = call("GET", f"/api/cards/{card_item['id']}")["card"]
data = card["data"]
check("description 已修改", data["description"] == "修改后的描述")
check("personality 已修改", data["personality"] == "活泼")
check("未知键 avatar 保留", card.get("avatar") == "none")
check("未知键 create_date 保留", card.get("create_date") == "2025-1-1")

print("== 备用开场白 / 标签 ==")
call("PUT", f"/api/cards/{card_item['id']}", {
    "fields": {}, "alternateGreetings": ["开场A", "开场B"], "tags": ["测试", "科幻"]})
data = call("GET", f"/api/cards/{card_item['id']}")["card"]["data"]
check("alternate_greetings 写入", data["alternate_greetings"] == ["开场A", "开场B"])
check("tags 写入", data["tags"] == ["测试", "科幻"])

print("== 整卡替换（原始JSON模式）==")
full = call("GET", f"/api/cards/{card_item['id']}")["card"]
full["data"]["first_mes"] = "新开场白"
call("PUT", f"/api/cards/{card_item['id']}", {"card": full})
data = call("GET", f"/api/cards/{card_item['id']}")["card"]["data"]
check("first_mes 更新", data["first_mes"] == "新开场白")
bad = call("PUT", f"/api/cards/{card_item['id']}", {"card": {"foo": 1}})
check("非法整卡被拒绝", "error" in (bad or {}))

print("== 世界书编辑 ==")
lore_items = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("测试书"))
lore = call("GET", f"/api/lore/{lore_items[0]['id']}")
check("条目数=1", len(lore["entries"]) == 1)
e0 = dict(lore["entries"][0])
e0["data"]["content"] = "修改后条目内容"
lore["entries"].append({
    "key": "1",
    "data": {"key": ["新词"], "content": "新条目", "comment": "条目二",
             "constant": False, "disable": False, "order": 100, "position": 0,
             "depth": 4, "probability": 100},
})
r = call("PUT", f"/api/lore/{lore_items[0]['id']}", {"entries": lore["entries"]})
check("保存条目数=2", r.get("count") == 2)
lore2 = call("GET", f"/api/lore/{lore_items[0]['id']}")
check("修改保留", lore2["entries"][0]["data"]["content"] == "修改后条目内容")
check("新增存在", any(e["data"].get("comment") == "条目二" for e in lore2["entries"]))

print("== 文本编辑与 JSON 校验 ==")
r = call("PUT", f"/api/text/{lore_items[0]['id']}", {"content": "{bad json"})
check("坏 JSON 被拒绝", "error" in (r or {}))
good = json.dumps(call("GET", f"/api/lore/{lore_items[0]['id']}") or {}, ensure_ascii=False)

print("== 收藏 / 标签 ==")
call("POST", f"/api/items/{card_item['id']}/favorite", {"fav": True})
fav_items = call("GET", "/api/items?fav=true")
check("收藏过滤", any(i["id"] == card_item["id"] for i in fav_items))
call("POST", f"/api/items/{card_item['id']}/tags", {"tags": ["常用", "测试tag"]})
tagged = call("GET", "/api/items?tag=" + urllib.parse.quote("常用"))
check("用户标签过滤", any(i["id"] == card_item["id"] for i in tagged))

print("== 内嵌世界书（A 计划）==")
card_items = call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("测试卡"))
check("卡片带内嵌书标记", card_items[0].get("hasCharacterBook") is True, f"entryCount={card_items[0].get('entryCount')}")
book = call("GET", f"/api/cards/{card_items[0]['id']}/book")
check("读取内嵌书 1 条", len(book["entries"]) == 1)
e0 = book["entries"][0]
check("Spec→ST 转换", e0["data"]["key"] == ["内置词"] and e0["data"]["order"] == 50 and e0["data"]["position"] == 0)
check("raw 原条目回传", e0["raw"] is not None and e0["raw"].get("selective") is True)
# 编辑：改内容并禁用，raw 原样回传
e0["data"]["content"] = "编辑后的内置内容"
e0["data"]["disable"] = True
r = call("PUT", f"/api/cards/{card_items[0]['id']}/book", {"entries": book["entries"]})
check("保存内嵌书", r.get("ok") is True and r.get("count") == 1)
book2 = call("GET", f"/api/cards/{card_items[0]['id']}/book")
e1 = book2["entries"][0]
check("编辑生效", e1["data"]["content"] == "编辑后的内置内容" and e1["data"]["disable"] is True)
card_now = call("GET", f"/api/cards/{card_items[0]['id']}")["card"]["data"]["character_book"]["entries"][0]
check("未编辑字段保留", card_now.get("selective") is True and card_now.get("id") == 42)
check("enabled 翻转正确", card_now.get("enabled") is False)

print("== 自动备份与还原 ==")
bk = call("GET", f"/api/items/{card_items[0]['id']}/backups")
check("卡片编辑产生备份", len(bk) >= 1, f"共 {len(bk)} 份")
oldest = bk[-1]
# 还原到最早备份，description 应回到"原始描述"
r = call("POST", f"/api/backups/{oldest['id']}/restore", {})
check("还原成功", r.get("ok") is True)
card_after = call("GET", f"/api/cards/{card_items[0]['id']}")["card"]["data"]
check("还原回旧内容", card_after["description"] == "原始描述", card_after["description"])
check("还原动作本身也产生了新备份", len(call("GET", f"/api/items/{card_items[0]['id']}/backups")) >= len(bk) + 1)
mid = call("GET", f"/api/items/{card_items[0]['id']}/backups")[0]
check("删除备份", call("DELETE", f"/api/backups/{mid['id']}", {}) .get("ok") is True)
stats = call("GET", "/api/backups/stats")
check("备份统计与开关", stats.get("autoBackup") is True and stats.get("maxPerFile") >= 1)

print("== 另存为（自动命名）==")
full_card = call("GET", f"/api/cards/{card_items[0]['id']}")["card"]
r = call("POST", f"/api/cards/{card_items[0]['id']}/saveas", {"card": full_card})
check("卡片另存为", r.get("ok") is True and "-副本" in r.get("fileName", ""), r.get("fileName"))
copy_id = r["id"]
copy_item = call("GET", f"/api/items/{copy_id}")
check("副本被索引为角色卡", copy_item.get("kind") == "character")
check("副本独立于原文件", copy_id != card_items[0]["id"])
call("POST", f"/api/items/{copy_id}/delete", {})  # 清理副本

lore_id2 = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("测试书"))[0]["id"]
lore_bk = call("GET", f"/api/lore/{lore_id2}")
r = call("POST", f"/api/lore/{lore_id2}/saveas", {"entries": lore_bk["entries"]})
check("世界书另存为", r.get("ok") is True and "-副本" in r.get("fileName", ""))
call("POST", f"/api/items/{r['id']}/delete", {})

r = call("POST", f"/api/cards/{card_items[0]['id']}/book/saveas", {"entries": book2["entries"]})
check("内嵌书导出独立文件", r.get("ok") is True and "-副本" in r["fileName"], r.get("fileName"))
call("POST", f"/api/items/{r['id']}/delete", {})

r = call("POST", f"/api/text/{card_items[0]['id']}/saveas", {"content": "{bad"})
check("另存为坏 JSON 被拒", "error" in (r or {}))
r = call("POST", f"/api/text/{card_items[0]['id']}/saveas", {"content": "{\"ok\":1}"})
check("文本另存为", r.get("ok") is True)
call("POST", f"/api/items/{r['id']}/delete", {})
call("PUT", f"/api/cards/{card_items[0]['id']}", {"fields": {"description": "原始描述"}})  # 恢复描述给重命名测试用
check("position 保持字符串", card_now.get("position") == "before_char")
check("索引条目数更新", call("GET", f"/api/items/{card_items[0]['id']}").get("entryCount") == 1)


print("== 重命名 / 移动 ==")
r = call("POST", f"/api/items/{card_item['id']}/rename", {"name": "改名卡"})
check("重命名返回新id", bool(r.get("id")))
new_id = r["id"]
item = call("GET", f"/api/items/{new_id}")
check("文件已改名", item["fileName"] == "改名卡.json")
check("卡片内名称不变", item.get("title") == "测试卡")
r = call("POST", f"/api/items/{new_id}/move", {"root": TESTDATA, "dir": "归档"})
new_id2 = r["id"]
item = call("GET", f"/api/items/{new_id2}")
check("目录已移动", item["relativeDir"].replace("/", "\\") == "归档")

print("== 删除（回收站）==")
r = call("POST", f"/api/items/{new_id2}/delete", {})
check("删除成功", r.get("ok") is True)
after = call("GET", "/api/items?q=" + urllib.parse.quote("改名卡"))
check("索引中已消失", len(after) == 0)

print("== 越权防护 ==")
# 用一个真实存在的条目测试越权 root
lore_items = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("测试书"))
guard_id = lore_items[0]["id"]
r = call("POST", f"/api/items/{guard_id}/move", {"root": "C:\\Windows", "dir": ""})
check("越权 root 被拒绝", r is not None and "error" in r)
r = call("POST", f"/api/items/{guard_id}/move", {"root": TESTDATA, "dir": "归档2"})
check("库内移动正常", r is not None and r.get("ok") is True)

print("== 三逻辑库（v0.4.2）==")
meta = call("GET", "/api/meta")
libs = meta.get("libraries") or []
check("libraries 存在三库", {l.get("key") for l in libs} == {"normal", "tavernST", "tavernTT"})
check("每库键齐全", all(
    all(k in l for k in ("key", "label", "total", "rootCount", "favorites", "kinds", "dirs", "tags"))
    for l in libs))
check("每库 kinds 8 类", all(len(l["kinds"]) == 8 for l in libs))
check("库内不变量 Σkinds==total", all(sum(k["count"] for k in l["kinds"]) == l["total"] for l in libs))
check("全局 total==Σ库 total", sum(l["total"] for l in libs) == meta["total"])
roots_by_source = {}
for r in meta.get("roots") or []:
    roots_by_source[r.get("source", "normal")] = roots_by_source.get(r.get("source", "normal"), 0) + 1
check("rootCount 与 roots 一致", all(
    l["rootCount"] == roots_by_source.get(l["key"], 0) for l in libs))
# 酒馆根未接入时应查零；接入后 dirs 含空根占位
st_items = call("GET", "/api/items?source=tavernST")
st_lib = next(l for l in libs if l["key"] == "tavernST")
check("tavernST 过滤与 meta 一致", len(st_items) == st_lib["total"])
if st_lib["rootCount"] == 0:
    check("未接入酒馆库查零", len(st_items) == 0)
normal_items = call("GET", "/api/items?source=normal")
check("source=normal 全部 rootSource==0", all(i["rootSource"] == 0 for i in normal_items))
combo = call("GET", "/api/items?source=normal&kind=character")
all_char = call("GET", "/api/items?kind=character")
check("source+kind 组合是子集", 0 < len(combo) <= len(all_char)
      if all_char else len(combo) == 0)
bad = call("GET", "/api/items?source=bogus")
check("非法 source 返回 400", isinstance(bad, dict) and bad.get("error") == "无效的库来源")
# dirs 闭环：普通库第一个目录的 count 与查询一致
normal_lib = next(l for l in libs if l["key"] == "normal")
if normal_lib["dirs"]:
    d0 = normal_lib["dirs"][0]
    q = call("GET", "/api/items?source=normal&dir=" + urllib.parse.quote(d0["dir"]))
    check("dirs 闭环（dir 查询==计数）", len(q) == d0["count"])

print(f"\n结果：{ok_count} 通过，{fail_count} 失败")
sys.exit(1 if fail_count else 0)
