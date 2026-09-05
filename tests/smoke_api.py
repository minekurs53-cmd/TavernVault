# -*- coding: utf-8 -*-
"""针对本地服务的 API 冒烟测试（写入操作只作用于 testdata 临时库根）。

连接信息默认从服务端写出的 server-connection.json 读取（--server 模式产物）；
TV_CONN / TV_BASE / TV_TOKEN 环境变量可覆写。

覆盖范围总览（2026-09-05 随 v0.7.2 全面复审）：
- 基础 CRUD：搜索 / 角色卡（字段·整卡·开场白·标签）/ 世界书（对象+数组容器）/ 文本与 JSON 校验 /
  收藏标签 / 内嵌世界书 / 另存为（自动命名，正向+逃逸回归）/ PNG 完整性 / 重命名移动删除 / 越权防护
- 可靠性合同：自动备份与还原（含满上限自逐出）/ 409 编辑并发 / 会话令牌 + Host 白名单 / 错误合同
- 多库与识别：三逻辑库聚合 / 格式识别回落（v0.6.1 回撤 5 类）/ 酒馆子目录探测
- 酒馆托管（v0.7.1 语义）：护栏 403 矩阵（rename/move/PUT×3）/ 导出副本全链路 / 强制备份（rename force）
- v0.6.0+：新建文件 6 类模板回路；v0.7.1：修改历史聚合与已删过滤 / meta.dataDir
- v0.7.2：文件监视自动重扫（直接落盘→自动入库/出库）
- v0.7.3：收纳入库（散乱夹具 → 分类落位/源不动/move/重名序号/四条负向合同）
- v0.7.6：内嵌世界书合入（ST/Spec 来源追加规范化/来源不动/酒馆卡 403）
- 永不纳入：reveal 的真实调用（会弹桌面资源管理器窗口，见 v0.7.1 事故记录）；前端逻辑
  （由 preset-model node 测试 + 浏览器 UI 实跑覆盖）
"""
import base64
import json
import os
import shutil
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib


def load_conn():
    base = os.environ.get("TV_BASE")
    token = os.environ.get("TV_TOKEN")
    if base and token:
        return base, token
    global CONN_PATH
    CONN_PATH = os.path.abspath(os.environ.get("TV_CONN", "testdata-server/server-connection.json"))
    if not os.path.exists(CONN_PATH):
        print(f"未找到连接文件 {CONN_PATH}。请先启动服务：")
        print("  ./src/TavernVault.App/bin/Release/net10.0-windows/TavernVault.exe "
              "--server --port=47999 --data=testdata-server &")
        sys.exit(2)
    with open(CONN_PATH, encoding="utf-8") as f:
        cfg = json.load(f)
    return cfg["url"], cfg["token"]


CONN_PATH = os.path.abspath(os.environ.get("TV_CONN", "testdata-server/server-connection.json"))
BASE, TOKEN = load_conn()
ok_count = 0
fail_count = 0


def call(method, path, body=None):
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("X-TV-Token", TOKEN)
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


def call_raw(method, path, headers=None):
    """无令牌原始请求：安全负向用例（返回 HTTP 状态码）。"""
    req = urllib.request.Request(BASE + path, method=method)
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req) as resp:
            return resp.status, resp.headers.get("Content-Type") or ""
    except urllib.error.HTTPError as ex:
        return ex.code, ex.headers.get("Content-Type") or ""


def call_code(method, path, body=None):
    """带令牌请求，只返回 HTTP 状态码（错误合同用例）。"""
    req = urllib.request.Request(BASE + path, method=method)
    req.add_header("X-TV-Token", TOKEN)
    data = None
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, data) as resp:
            return resp.status
    except urllib.error.HTTPError as ex:
        return ex.code


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
# 清理上次运行残留（移动段目标冲突、另存为副本），保证夹具自足可重复
for sub in ("归档", "归档2"):
    d = os.path.join(TESTDATA, sub)
    if os.path.isdir(d):
        for fn in os.listdir(d):
            if fn.endswith(".json"):
                os.remove(os.path.join(d, fn))
for fn in os.listdir(TESTDATA):
    if "-副本" in fn:
        os.remove(os.path.join(TESTDATA, fn))
# 数据目录备份/缩略图同理：上轮运行的备份会混入本轮 manifest（按文件名归档），清掉保证可重复。
# 路径基于连接文件所在目录推导（v0.5.1 修复 N2：此前错误拼出双层 testdata-server 导致清理空操作）
conn_dir = os.path.dirname(CONN_PATH)
for sub in ("backups", "thumbs"):
    d = os.path.join(conn_dir, sub)
    if os.path.isdir(d):
        shutil.rmtree(d, ignore_errors=True)
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

print("== 内嵌世界书合入（v0.7.6）==")
# 独立书（1 条 ST + 1 条 Spec 数组）→ 专用目标卡内嵌书：追加、规范化、来源不动。
# 用独立目标卡：导入的备份会占用该卡保留份数，不能挤转测试卡的备份历史（备份还原段按最早备份断言）。
imp_book = {"name": "合入来源", "entries": [
    {"key": ["合入词"], "content": "ST来源内容", "comment": "合入条目",
     "disable": False, "order": 5, "position": 0},
]}
imp_spec = {"name": "合入来源Spec", "entries": [
    {"keys": ["Spec合入词"], "content": "Spec来源内容", "enabled": False,
     "insertion_order": 9, "id": 3, "extensions": {}},
]}
with open(os.path.join(TESTDATA, "合入来源.json"), "w", encoding="utf-8") as f:
    json.dump(imp_book, f, ensure_ascii=False)
imp_card = {"spec": "chara_card_v2", "spec_version": "2.0", "data": {
    "name": "合入目标卡", "description": "",
    "character_book": {"name": "内嵌", "entries": {"0": {
        "key": ["已有词"], "content": "已有内容", "comment": "已有条目",
        "disable": False, "order": 1, "position": 0}}}}}
with open(os.path.join(TESTDATA, "合入目标卡.json"), "w", encoding="utf-8") as f:
    json.dump(imp_card, f, ensure_ascii=False)
call("POST", "/api/rescan")
imp_id = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("合入来源"))[0]["id"]
imp_card_id = call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("合入目标卡"))[0]["id"]

book_before = call("GET", f"/api/cards/{imp_card_id}/book")
n_before = len(book_before["entries"])
r = call("POST", f"/api/cards/{imp_card_id}/book/import", {"sourceId": imp_id})
check("合入 ST 来源 ok+计数", (r or {}).get("ok") is True and (r or {}).get("added") == 1
      and (r or {}).get("total") == n_before + 1, str(r)[:100])
book_after = call("GET", f"/api/cards/{imp_card_id}/book")
check("合入条目在内嵌书尾部", book_after["entries"][-1]["data"]["content"] == "ST来源内容"
      and book_after["entries"][0]["data"]["content"] == "已有内容")  # 已有条目仍在

# Spec 数组来源规范化进同容器（enabled→disable 等），再合一次
with open(os.path.join(TESTDATA, "合入来源.json"), "w", encoding="utf-8") as f:
    json.dump(imp_spec, f, ensure_ascii=False)
call("POST", "/api/rescan")
r = call("POST", f"/api/cards/{imp_card_id}/book/import", {"sourceId": imp_id})
check("合入 Spec 来源 ok", (r or {}).get("ok") is True and (r or {}).get("added") == 1, str(r)[:80])
book_after2 = call("GET", f"/api/cards/{imp_card_id}/book")
last = book_after2["entries"][-1]["data"]
check("Spec 条目规范化", last.get("disable") is True and last.get("content") == "Spec来源内容"
      and "enabled" not in last, str(last)[:100])
check("来源文件未被修改",
      json.load(open(os.path.join(TESTDATA, "合入来源.json"), encoding="utf-8")) == imp_spec)

# 负向：来源不是世界书 / 来源不存在
code = call_code("POST", f"/api/cards/{imp_card_id}/book/import", {"sourceId": "unknownid0000"})
check("合入未知来源 404/400", code in (400, 404), f"HTTP {code}")
# 清理：删除来源夹具（内嵌书改动留在测试卡上，属预期）
for q in ("合入来源", "合入目标卡"):
    for row in call("GET", "/api/items?q=" + urllib.parse.quote(q)):
        call("POST", f"/api/items/{row['id']}/delete", {})
call("POST", "/api/rescan")
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

print("== 满上限还原（v0.5.1 N1 回归）==")
with open(os.path.join(TESTDATA, "轮转卡.json"), "w", encoding="utf-8") as f:
    json.dump({"spec": "chara_card_v2", "spec_version": "2.0",
               "data": {"name": "轮转卡", "description": "初始"}}, f, ensure_ascii=False)
call("POST", "/api/rescan")
rot = call("GET", "/api/items?q=" + urllib.parse.quote("轮转卡"))[0]
for i in range(6):  # 连续 6 次编辑把默认 5 份的保留窗口堆满
    call("PUT", f"/api/cards/{rot['id']}", {"fields": {"description": f"轮转{i}"}})
bks = call("GET", f"/api/items/{rot['id']}/backups")
check("备份堆满保留窗口", len(bks) == 5, f"共 {len(bks)} 份")
r = call("POST", f"/api/backups/{bks[-1]['id']}/restore", {})
check("满上限还原最旧成功", r.get("ok") is True, str(r))
after = call("GET", f"/api/cards/{rot['id']}")["card"]["data"]["description"]
check("还原回最旧内容", after == "轮转0", after)
check("还原后仍保留 5 份", len(call("GET", f"/api/items/{rot['id']}/backups")) == 5)
call("POST", f"/api/items/{rot['id']}/delete", {})

print("== 导出路径逃逸（v0.5.1 P1-6 回归）==")
with open(os.path.join(TESTDATA, "路径卡.json"), "w", encoding="utf-8") as f:
    json.dump({"spec": "chara_card_v2", "spec_version": "2.0",
               "data": {"name": "..\\..\\evil 路径卡", "description": "x"}}, f, ensure_ascii=False)
call("POST", "/api/rescan")
evil = call("GET", "/api/items?q=" + urllib.parse.quote("路径卡"))[0]
r = call("POST", f"/api/cards/{evil['id']}/book/saveas", {"entries": []})
fn = r.get("fileName", "")
check("导出成功", r.get("ok") is True, fn)
check("导出文件名无路径分隔符", bool(fn) and "/" not in fn and "\\" not in fn, fn)
check("导出文件落在库内", os.path.isfile(os.path.join(TESTDATA, fn)))
call("POST", f"/api/items/{r['id']}/delete", {})
call("POST", f"/api/items/{evil['id']}/delete", {})

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


print("== PNG 卡完整性（v0.5.0 数据损坏回归）==")
PNG_SIG = b"\x89PNG\r\n\x1a\n"


def png_chunk(ctype, data):
    return struct.pack(">I", len(data)) + ctype + data + struct.pack(">I", zlib.crc32(ctype + data) & 0xFFFFFFFF)


def make_png_card(path, card_json):
    ihdr = struct.pack(">IIBBBBB", 1, 1, 8, 2, 0, 0, 0)  # 1x1、8bit、真彩
    idat = zlib.compress(b"\x00\xff\x00\x7f")             # 滤波字节 + 1 像素（WPF 可解码）
    chara = base64.b64encode(json.dumps(card_json, ensure_ascii=False).encode("utf-8"))
    with open(path, "wb") as f:
        f.write(PNG_SIG + png_chunk(b"IHDR", ihdr) + png_chunk(b"tEXt", b"chara\x00" + chara)
                + png_chunk(b"IDAT", idat) + png_chunk(b"IEND", b""))


def read_png_chunks(path):
    with open(path, "rb") as f:
        data = f.read()
    chunks = {}
    pos = 8
    while pos + 12 <= len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        chunks[data[pos + 4:pos + 8].decode("latin-1")] = data[pos + 8:pos + 8 + length]
        pos += 12 + length
    return chunks


png_path = os.path.join(TESTDATA, "图像卡.png")
make_png_card(png_path, {"spec": "chara_card_v2", "spec_version": "2.0",
                         "data": {"name": "图像卡", "description": "图像卡描述"}})
call("POST", "/api/rescan")
png_items = call("GET", "/api/items?q=" + urllib.parse.quote("图像卡"))
check("PNG 卡被识别", len(png_items) == 1 and png_items[0]["kind"] == "character"
      and png_items[0].get("hasEmbeddedCard") is True)
png_id = png_items[0]["id"]
orig_idat = read_png_chunks(png_path).get("IDAT")

png_card = call("GET", f"/api/cards/{png_id}")["card"]
png_card["data"]["description"] = "编辑后的图像卡描述"
r = call("POST", f"/api/cards/{png_id}/saveas", {"card": png_card})
check("PNG 另存为成功", r.get("ok") is True, r.get("fileName", ""))
copy_path = os.path.join(TESTDATA, r["fileName"])
copy_chunks = read_png_chunks(copy_path)
check("副本含 IHDR/IDAT/IEND", all(t in copy_chunks for t in ("IHDR", "IDAT", "IEND")),
      str(sorted(copy_chunks)))
check("副本 IDAT 与原图一致（图像保留）", copy_chunks.get("IDAT") == orig_idat)
copy_card = call("GET", f"/api/cards/{r['id']}")["card"]
check("副本卡片为编辑后内容", copy_card["data"]["description"] == "编辑后的图像卡描述")

call("PUT", f"/api/cards/{r['id']}", {"fields": {"description": "再编辑一次"}})
check("PUT 编辑后 IDAT 字节不变", read_png_chunks(copy_path).get("IDAT") == orig_idat)

code, ctype = call_raw("GET", f"/api/thumb/{png_id}?token={urllib.parse.quote(TOKEN)}")
check("缩略图生成（query 令牌通道）", code == 200 and ctype.startswith("image/jpeg"), f"HTTP {code}")
call("POST", f"/api/items/{r['id']}/delete", {})


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
check("每库 kinds 8 类", all(len(l["kinds"]) == 8 for l in libs))  # v0.6.1 回撤 5 类官方模板分类
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
# dirs 闭环：普通库第一个非空目录的 count 与查询一致（dir="" 根目录无法与"不过滤"区分，跳过）
normal_lib = next(l for l in libs if l["key"] == "normal")
d0 = next((d for d in normal_lib["dirs"] if d["dir"]), None)
if d0:
    q = call("GET", "/api/items?source=normal&dir=" + urllib.parse.quote(d0["dir"]))
    check("dirs 闭环（dir 查询==计数）", len(q) == d0["count"], f"dir={d0['dir']} count={d0['count']} got={len(q)}")

print("== 编辑并发防护（v0.5.0 409）==")
guard_id = call("GET", "/api/items?kind=lorebook")[0]["id"]  # 测试书在前段被重命名/移动过，取当前 id
conc_item = call("GET", f"/api/items/{guard_id}")
stale = conc_item["modifiedAt"]
cur_text = call("GET", f"/api/text/{guard_id}")["content"]
time.sleep(1.5)  # 让 stale 与外部改动间隔超过 1s mtime 容差（比较的是文件 mtime 而非钟表时间）
with open(conc_item["fullPath"], "a", encoding="utf-8") as f:
    f.write(" ")  # 外部改动文件（模拟另一窗口/程序）
conflict = call("PUT", f"/api/text/{guard_id}",
                {"content": cur_text, "expectedModified": stale})
check("过期 modified 被拒 409", conflict is not None and "已被外部" in (conflict.get("error") or ""),
      str(conflict)[:80])
call("POST", "/api/rescan")  # 模拟用户"重新打开条目"：重扫后索引跟进外部改动
fresh = call("GET", f"/api/items/{guard_id}")["modifiedAt"]
ok2 = call("PUT", f"/api/text/{guard_id}",
           {"content": cur_text, "expectedModified": fresh})
check("新鲜 modified 保存成功并回传 modifiedAt", ok2.get("ok") is True and ok2.get("modifiedAt"),
      str(ok2)[:80])

print("== 安全防护（v0.5.0 会话令牌 + Host 白名单）==")
code, _ = call_raw("GET", "/api/meta")
check("无令牌被拒 401", code == 401, f"HTTP {code}")
code, _ = call_raw("GET", "/api/meta", {"X-TV-Token": "wrong-token-0123456789"})
check("错令牌被拒 401", code == 401, f"HTTP {code}")
code, _ = call_raw("GET", "/api/meta", {"Host": "evil.example.com", "X-TV-Token": TOKEN})
check("伪造 Host 被拒 403", code == 403, f"HTTP {code}")
code, ctype = call_raw("GET", "/")
check("静态文件无令牌可达", code == 200 and "text/html" in ctype, f"HTTP {code}")

print("== 酒馆护栏（v0.5.2 回归）==")
# 自建假酒馆根（tavernST）：不需要真实安装。段内自清理：条目进回收站 + 移除根 + 重扫。
# 注意：酒馆根必须是独立目录，不能嵌在 TESTDATA 里——扫描器按路径去重且先扫到的根生效，
# 嵌套路径会被外层 testdata（normal）抢注，rootSource 永远到不了 tavernST。
# 放在 .smoke/（gitignore 内，testdata 的兄弟目录）：%TEMP% 常是 8.3 短路径（HUANGY~1），
# 服务端 GetFullPath 会展开成长路径，路径回比就变了。
TAVERN = os.path.abspath(os.path.join(".smoke", "酒馆源"))
os.makedirs(TAVERN, exist_ok=True)
with open(os.path.join(TAVERN, "酒馆卡.json"), "w", encoding="utf-8") as f:
    json.dump({"spec": "chara_card_v2", "spec_version": "2.0",
               "data": {"name": "酒馆卡", "description": "酒馆卡描述"}}, f, ensure_ascii=False)
with open(os.path.join(TAVERN, "酒馆书.json"), "w", encoding="utf-8") as f:
    json.dump({"entries": {"0": {"key": ["酒馆词"], "content": "酒馆内容", "comment": "酒馆条目",
                                 "constant": False, "disable": False, "order": 1,
                                 "position": 0, "depth": 4, "probability": 100}}}, f, ensure_ascii=False)
r = call("POST", "/api/roots", {"path": TAVERN, "source": "tavernST"})
check("注册 tavernST 根", (r or {}).get("ok") is True and any(
    os.path.normcase(rr.get("path") or "") == os.path.normcase(TAVERN)
    and rr.get("source") == "tavernST" for rr in r.get("roots") or []), str(r)[:80])
tcard = call("GET", "/api/items?source=tavernST&q=" + urllib.parse.quote("酒馆卡"))
tlore = call("GET", "/api/items?source=tavernST&kind=lorebook&q=" + urllib.parse.quote("酒馆书"))
check("酒馆条目入索引且 rootSource=tavernST",
      len(tcard) == 1 and len(tlore) == 1 and tcard[0]["rootSource"] == 1,
      str([i["fileName"] for i in tcard + tlore]))
tcard_id, tlore_id = tcard[0]["id"], tlore[0]["id"]

r = call("POST", "/api/tavern/detect")
found = (r or {}).get("found")
check("detect 返回 found 列表", isinstance(found, list), f"{len(found or [])} 项")
check("detect 每项含 source/label/subdirs",
      all(isinstance(d, dict) and all(k in d for k in ("source", "label", "subdirs")) for d in found or []))

code = call_code("POST", "/api/tavern/connect", {"source": "normal"})
r = call("POST", "/api/tavern/connect", {"source": "normal"})
check("connect 无效酒馆来源 400", code == 400 and (r or {}).get("error") == "无效的酒馆来源", f"HTTP {code}")

code = call_code("POST", f"/api/items/{tcard_id}/rename", {"name": "酒馆卡改名"})
check("rename 无 force 403", code == 403, f"HTTP {code}")
r = call("POST", f"/api/items/{tcard_id}/rename", {"name": "酒馆卡改名", "force": True})
check("rename 带 force 成功", (r or {}).get("ok") is True, str(r)[:60])
tcard_id = r["id"]  # 重命名后 id 随路径变化

code = call_code("POST", f"/api/items/{tlore_id}/move", {"root": TAVERN, "dir": ""})
check("move 无 force 403", code == 403, f"HTTP {code}")

# v0.7.1：酒馆来源禁止就地编辑（PUT 403，cards/text/lore 三路）——
# 实测酒馆不实时读外部修改且可能用内存旧数据回写覆盖，编辑走「导出副本」
card_desc_before = call("GET", f"/api/cards/{tcard_id}")["card"]["data"].get("description")
code = call_code("PUT", f"/api/cards/{tcard_id}", {"fields": {"description": "不应写入"}})
check("酒馆源 cards PUT 403", code == 403, f"HTTP {code}")
code = call_code("PUT", f"/api/text/{tlore_id}", {"content": "{}"})
check("酒馆源 text PUT 403", code == 403, f"HTTP {code}")
lore_cur = call("GET", f"/api/lore/{tlore_id}")
code = call_code("PUT", f"/api/lore/{tlore_id}", {"entries": lore_cur.get("entries") or [],
                                                  "container": lore_cur.get("container") or "object"})
check("酒馆源 lore PUT 403", code == 403, f"HTTP {code}")
code = call_code("POST", f"/api/cards/{tcard_id}/book/import", {"sourceId": tlore_id})
check("酒馆卡内嵌书合入 403", code == 403, f"HTTP {code}")
check("403 未写入文件", call("GET", f"/api/cards/{tcard_id}")["card"]["data"].get("description") == card_desc_before,
      str(card_desc_before)[:60])

# 导出副本：酒馆源 → 第一个局外库根（TESTDATA），字节级复制，副本可直接编辑
r = call("POST", f"/api/items/{tcard_id}/export", {})
check("酒馆源导出副本 ok", (r or {}).get("ok") is True and "-副本" in (r or {}).get("fileName", ""), str(r)[:80])
exp_items = [i for i in call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("酒馆卡"))
             if i["fileName"].startswith("酒馆卡改名-副本")]
check("导出副本入库（普通源）", len(exp_items) == 1 and exp_items[0]["rootSource"] == 0,
      str([(i["fileName"], i["rootSource"]) for i in exp_items]))
r = call("PUT", f"/api/cards/{exp_items[0]['id']}", {"fields": {"description": "导出后可编辑"}})
check("导出副本可编辑", (r or {}).get("ok") is True, str(r)[:60])
for row in exp_items:  # 副本清理（回收站）
    call("POST", f"/api/items/{row['id']}/delete", {})

png_items = call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("图像卡"))
check("普通卡夹具就绪", len(png_items) == 1 and png_items[0]["fileName"] == "图像卡.png",
      str([i["fileName"] for i in png_items]))
png_id = png_items[0]["id"]
code = call_code("POST", f"/api/items/{png_id}/export", {})
check("局外源导出 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/unknownid0000/export", {})
check("export 未知条目 404", code == 404, f"HTTP {code}")

# 强制备份：酒馆源无视 autoBackup 开关（rename force 是仅存的酒馆写入路径）；普通源关闭后不备
call("POST", "/api/settings/backup", {"autoBackup": False})
bk_before = len(call("GET", f"/api/items/{tcard_id}/backups"))
r = call("POST", f"/api/items/{tcard_id}/rename", {"name": "酒馆卡改名2", "force": True})
bk_after = call("GET", f"/api/items/{tcard_id}/backups")
check("酒馆源无视开关仍备份", (r or {}).get("ok") is True and len(bk_after) == bk_before + 1,
      f"{bk_before} → {len(bk_after)}")
tcard_id = r["id"]  # 重命名后 id 随路径变化

png_bk_before = len(call("GET", f"/api/items/{png_id}/backups"))
r = call("PUT", f"/api/cards/{png_id}", {"fields": {}})
png_bk_after = call("GET", f"/api/items/{png_id}/backups")
check("普通源关闭后不备份", (r or {}).get("ok") is True and len(png_bk_after) == png_bk_before,
      f"{png_bk_before} → {len(png_bk_after)}")

# settings/backup 负向合同（T3）：clamp / 相对路径 / 空串恢复默认
r = call("POST", "/api/settings/backup", {"maxPerFile": 0})
stats = call("GET", "/api/backups/stats")
check("maxPerFile 0 被 clamp 为 1", (r or {}).get("maxPerFile") == 1 and stats.get("maxPerFile") == 1, str(r)[:60])
code = call_code("POST", "/api/settings/backup", {"backupDir": "relative/path"})
r = call("POST", "/api/settings/backup", {"backupDir": "relative/path"})
check("相对路径 backupDir 400", code == 400 and "error" in (r or {}), f"HTTP {code}")
r = call("POST", "/api/settings/backup", {"backupDir": ""})
stats = call("GET", "/api/backups/stats")
check("空 backupDir 恢复默认目录", (r or {}).get("dir") == stats.get("defaultDir"), str(r)[:60])
# 恢复设置（同数据目录二连跑的前置条件：maxPerFile=5 供"满上限还原"段使用）
r = call("POST", "/api/settings/backup", {"autoBackup": True, "maxPerFile": 5})
check("恢复 autoBackup=true maxPerFile=5",
      (r or {}).get("autoBackup") is True and (r or {}).get("maxPerFile") == 5, str(r)[:60])

# 段末清理：酒馆条目进回收站 → 移除根 → 重扫 → 删除空目录
for tid in (tcard_id, tlore_id):
    call("POST", f"/api/items/{tid}/delete", {})
call("DELETE", "/api/roots", {"path": TAVERN})
call("POST", "/api/rescan")
check("酒馆根已移除", all(os.path.normcase((rr or {}).get("path") or "") != os.path.normcase(TAVERN)
                        for rr in call("GET", "/api/meta").get("roots") or []))
shutil.rmtree(TAVERN, ignore_errors=True)
try: os.rmdir(os.path.dirname(TAVERN))  # .smoke 父目录空了就一并撤掉
except OSError: pass

print("== 格式识别回落（v0.6.1 回撤 5 类官方模板分类）==")
# 夹具字段取自官方真实文件：
#   textgen ← default/content/presets/textgen/Universal-Light.json
#   instruct ← default/content/presets/instruct/ChatML.json
#   context ← default/content/presets/context/Default.json
#   sysprompt ← default/content/presets/sysprompt/Blank.json
#   quickreplies ← quick-reply/src/QuickReplySet.js 的 v2 序列化结构
# v0.6.1 起这 5 类不再设专属分类：官方模板 JSON 回落"文本"或"脚本"（原文编辑器可用）。
# 段内自清理（条目进回收站 + 重扫）；段前先清残留文件，同数据目录可重复跑。
V60_DIR = TESTDATA
V60_FIXTURES = {
    "冒烟文本预设.json": ("text", {
        "temp": 1.25, "temperature_last": False, "top_p": 1, "top_k": 0, "top_a": 0,
        "tfs": 1, "typical_p": 1, "min_p": 0.1, "rep_pen": 1, "rep_pen_range": 0,
        "smoothing_factor": 0, "add_bos_token": True, "ban_eos_token": False,
        "skip_special_tokens": True, "mirostat_mode": 0, "mirostat_tau": 5, "mirostat_eta": 0.1,
        "sampler_priority": ["repetition_penalty", "temperature"],
    }),
    "冒烟指令模板.json": ("text", {
        "input_sequence": "<|im_start|>user", "output_sequence": "<|im_start|>assistant",
        "last_output_sequence": "", "system_sequence": "<|im_start|>system",
        "stop_sequence": "<|im_end|>", "wrap": True, "macro": True,
        "names_behavior": "force", "output_suffix": "<|im_end|>\n", "name": "ChatML",
    }),
    "冒烟上下文模板.json": ("text", {
        "story_string": "{{#if system}}{{system}}\n{{/if}}{{trim}}",
        "example_separator": "***", "chat_start": "***",
        "use_stop_strings": False, "names_as_stop_strings": True,
        "story_string_position": 0, "story_string_depth": 1, "name": "Default",
    }),
    "冒烟系统提示.json": ("script", {
        "name": "Blank", "content": "", "post_history": "",
    }),
    "冒烟快捷回复.json": ("text", {
        "version": 2, "name": "我的快捷回复", "disableSend": False,
        "placeBeforeInput": False, "injectInput": False,
        "qrList": [{"id": 1, "label": "继续", "showLabel": True, "title": "",
                    "message": "/continue", "contextList": [], "isHidden": False,
                    "executeOnAi": False}],
        "idIndex": 1,
    }),
}
for fn in list(V60_FIXTURES) + ["冒烟误收预设.json"]:  # 清理上一轮残留（中途失败时文件可能还在）
    p = os.path.join(V60_DIR, fn)
    if os.path.exists(p):
        os.remove(p)

for fn, (_, fixture) in V60_FIXTURES.items():
    with open(os.path.join(V60_DIR, fn), "w", encoding="utf-8") as f:
        json.dump(fixture, f, ensure_ascii=False)
call("POST", "/api/rescan")
for fn, (kind, _) in V60_FIXTURES.items():
    stem = fn[:-len(".json")]
    found = call("GET", "/api/items?q=" + urllib.parse.quote(stem))
    check(f"识别 {kind}", len(found) == 1 and found[0]["kind"] == kind,
          str([(i["fileName"], i["kind"]) for i in found]))

# 优先级回归：裸 {name, content} 与官方 sysprompt 三键文件同判脚本（v0.6.1 起 sysprompt 无专属分类）
with open(os.path.join(V60_DIR, "冒烟普通脚本.json"), "w", encoding="utf-8") as f:
    json.dump({"name": "脚本", "content": "console.log(1)"}, f, ensure_ascii=False)
call("POST", "/api/rescan")
plain = call("GET", "/api/items?q=" + urllib.parse.quote("冒烟普通脚本"))
check("裸 name+content 仍为 script", len(plain) == 1 and plain[0]["kind"] == "script",
      str([(i["fileName"], i["kind"]) for i in plain]))

# 误收回归（v0.6.1 移除 textgen 规则的动机之一）：采样字段再多，只要带 prompts 数组就恒判预设
with open(os.path.join(V60_DIR, "冒烟误收预设.json"), "w", encoding="utf-8") as f:
    misread = dict(V60_FIXTURES["冒烟文本预设.json"][1])
    misread["prompts"] = [{"identifier": "main"}]
    json.dump(misread, f, ensure_ascii=False)
call("POST", "/api/rescan")
misread_items = call("GET", "/api/items?q=" + urllib.parse.quote("冒烟误收预设"))
check("采样字段+prompts 恒判预设", len(misread_items) == 1 and misread_items[0]["kind"] == "preset",
      str([(i["fileName"], i["kind"]) for i in misread_items]))

# ---- Spec V2 数组世界书：GET 转 ST + container=array，PUT 保形合并，磁盘容器仍为数组 ----
ARR_ORIGINAL = {
    "name": "冒烟数组书",
    "description": "Spec V2 / NovelAI 导出：entries 为数组",
    "entries": [
        {"keys": ["词A"], "content": "内容A", "comment": "条目A", "enabled": True,
         "insertion_order": 10, "position": "before_char", "id": 7, "selective": True,
         "use_regex": False, "extensions": {"depth": 4, "use_probability": 100}},
        {"keys": ["词B"], "secondary_keys": ["次B"], "content": "内容B", "enabled": False,
         "insertion_order": 20, "position": "after_char", "constant": True, "id": 9,
         "extensions": {}},
    ],
}
arr_path = os.path.join(V60_DIR, "冒烟数组书.json")
if os.path.exists(arr_path):
    os.remove(arr_path)
with open(arr_path, "w", encoding="utf-8") as f:
    json.dump(ARR_ORIGINAL, f, ensure_ascii=False)
call("POST", "/api/rescan")
arr_items = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("冒烟数组书"))
check("数组世界书识别为 lorebook", len(arr_items) == 1 and arr_items[0]["kind"] == "lorebook",
      str([(i["fileName"], i["kind"]) for i in arr_items]))
arr_id = arr_items[0]["id"]

lore = call("GET", f"/api/lore/{arr_id}")
check("GET 标记 container=array", lore.get("container") == "array", str(lore.get("container")))
check("数组条目数=2", len(lore["entries"]) == 2)
e = lore["entries"][0]
check("Spec→ST 转换", e["data"]["key"] == ["词A"] and e["data"]["order"] == 10
      and e["data"]["position"] == 0 and e["data"]["disable"] is False)
check("raw 原条目回传", e.get("raw") is not None and e["raw"].get("id") == 7
      and e["raw"].get("selective") is True)

# 编辑一条（raw 原样回传），PUT 带 container=array
e["data"]["content"] = "内容A改"
e["data"]["disable"] = True
r = call("PUT", f"/api/lore/{arr_id}", {"entries": lore["entries"], "container": "array"})
check("数组容器保存成功", (r or {}).get("ok") is True and (r or {}).get("count") == 2, str(r)[:80])

lore2 = call("GET", f"/api/lore/{arr_id}")
check("编辑生效且仍为 array", lore2.get("container") == "array"
      and lore2["entries"][0]["data"]["content"] == "内容A改"
      and lore2["entries"][0]["data"]["disable"] is True)
check("raw 字段保留", lore2["entries"][0]["raw"].get("id") == 7
      and lore2["entries"][0]["raw"].get("selective") is True)

with open(arr_items[0]["fullPath"], encoding="utf-8") as f:
    on_disk = json.load(f)
check("磁盘容器仍为数组", isinstance(on_disk["entries"], list))
edited = on_disk["entries"][0]
check("磁盘条目 raw 字段保留", edited.get("id") == 7 and edited.get("selective") is True
      and edited.get("use_regex") is False and edited.get("keys") == ["词A"]
      and edited.get("extensions", {}).get("depth") == 4)
check("磁盘条目编辑翻转 enabled", edited.get("enabled") is False and edited.get("content") == "内容A改")
check("未编辑条目原样", on_disk["entries"][1] == ARR_ORIGINAL["entries"][1])
object_lore = call("GET", "/api/items?kind=lorebook&q=" + urllib.parse.quote("测试书"))
check("对象容器默认行为不回归", call("GET", f"/api/lore/{object_lore[0]['id']}").get("container") == "object")

# 段末清理：全部夹具进回收站 → 重扫
for fn in list(V60_FIXTURES) + ["冒烟误收预设.json"]:
    it = call("GET", "/api/items?q=" + urllib.parse.quote(fn[:-len(".json")]))
    for row in it:
        call("POST", f"/api/items/{row['id']}/delete", {})
call("POST", f"/api/items/{arr_id}/delete", {})
plain_id = call("GET", "/api/items?q=" + urllib.parse.quote("冒烟普通脚本"))
for row in plain_id:
    call("POST", f"/api/items/{row['id']}/delete", {})
call("POST", "/api/rescan")
check("v0.6.1 夹具已清理", all(
    call("GET", "/api/items?q=" + urllib.parse.quote(fn[:-len(".json")])) == []
    for fn in list(V60_FIXTURES) + ["冒烟数组书.json", "冒烟普通脚本.json", "冒烟误收预设.json"]))

print("== 新建文件（v0.6.0，v0.6.1 收敛为 6 类）==")
# 6 个可新建 kind（archive/other 不支持）。逐一 create → 识别回路 → 可编辑 → 删除清理。
CREATE_KINDS = {
    "character": "角色卡", "lorebook": "世界书", "preset": "预设",
    "theme": "美化", "script": "脚本", "text": "文本",
}
created_ids = []


def create_one(kind, name, root=None):
    body = {"kind": kind, "name": name}
    if root:
        body["root"] = root
    return call("POST", "/api/items/create", body)


# 段前清理上一轮残留（同名文件会让本轮命中" (2)"序号，断言按 id 不受影响，但磁盘会积垃圾）
for fn in list(os.listdir(TESTDATA)):
    if fn.startswith("冒烟新建"):
        os.remove(os.path.join(TESTDATA, fn))

for kind, label in CREATE_KINDS.items():
    r = create_one(kind, "冒烟新建" + label)
    check(f"create {kind} 返回 ok+id", (r or {}).get("ok") is True and bool((r or {}).get("id")), str(r)[:80])
    if not (r or {}).get("id"):
        continue
    rid = r["id"]
    created_ids.append(rid)
    check(f"create {kind} 文件名正确", r.get("fileName", "").startswith("冒烟新建" + label)
          and "/" not in r.get("fileName", "") and "\\" not in r.get("fileName", ""), r.get("fileName", ""))
    # 识别回路：创建出的文件必须被自家识别回对应 kind（硬验收）
    item = call("GET", f"/api/items/{rid}")
    check(f"识别回路 {kind}", item.get("kind") == kind, f"got={item.get('kind')}")

# 可编辑确认：character/lorebook 走专用端点，其余（.json/.txt）走文本端点
char_id = created_ids[0]
r = call("PUT", f"/api/cards/{char_id}", {"fields": {"description": "新建卡描述"}})
check("新建角色卡可编辑", (r or {}).get("ok") is True, str(r)[:60])
check("新建角色卡编辑生效",
      call("GET", f"/api/cards/{char_id}")["card"]["data"]["description"] == "新建卡描述")

lore_id = created_ids[1]
lore = call("GET", f"/api/lore/{lore_id}")
check("新建世界书 container=object 空条目", lore.get("container") == "object" and lore.get("entries") == [],
      str(lore)[:60])
r = call("PUT", f"/api/lore/{lore_id}", {"entries": [
    {"key": "0", "data": {"key": ["词"], "content": "内容", "comment": "条目",
                          "constant": False, "disable": False, "order": 1,
                          "position": 0, "depth": 4, "probability": 100}}]})
check("新建世界书可编辑", (r or {}).get("ok") is True and (r or {}).get("count") == 1, str(r)[:60])

for rid, kind in [(rid, k) for rid, k in zip(created_ids, CREATE_KINDS)
                  if k not in ("character", "lorebook")]:
    cur = call("GET", f"/api/text/{rid}")["content"]
    if kind == "text":
        content = "冒烟新建文本内容"
    else:  # .json 保留合法 JSON，只改一个字段
        obj = json.loads(cur)
        obj.setdefault("__smoke__", True)
        content = json.dumps(obj, ensure_ascii=False)
    r = call("PUT", f"/api/text/{rid}", {"content": content})
    check(f"新建 {kind} 可文本编辑", (r or {}).get("ok") is True, str(r)[:60])
    check(f"新建 {kind} 编辑后识别不变", call("GET", f"/api/items/{rid}").get("kind") == kind)

# 重名：同名再建 → ok 且文件名带 " (2)" 序号
r1 = create_one("text", "冒烟新建重名")
r2 = create_one("text", "冒烟新建重名")
check("重名自动加序号", (r1 or {}).get("ok") is True and (r2 or {}).get("ok") is True
      and r2.get("fileName") == "冒烟新建重名 (2).txt", f"{r1 and r1.get('fileName')} / {r2 and r2.get('fileName')}")
created_ids += [r1["id"], r2["id"]]

# 显式 root：普通库根可指定
r = create_one("text", "冒烟新建指定根", root=TESTDATA)
check("显式普通 root 创建成功", (r or {}).get("ok") is True, str(r)[:60])
created_ids.append(r["id"])

# 负向合同
code = call_code("POST", "/api/items/create", {"kind": "archive", "name": "冒烟新建压缩包"})
check("kind=archive 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "other", "name": "冒烟新建其他"})
check("kind=other 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "textgen", "name": "冒烟新建文本预设"})
check("kind=textgen 400（v0.6.1 已回撤）", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "不存在的类型", "name": "x"})
check("非法 kind 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "text", "name": ""})
check("name 空 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "text", "name": "..."})
check("name 清洗后为空 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/items/create", {"kind": "text", "name": "冒烟新建越权", "root": "D:\\不存在的根\\nope"})
check("非法 root 400", code == 400, f"HTTP {code}")

# 酒馆来源根禁止新建（护栏哲学）：构造 tavernST 根再试，完事移除。
# 同酒馆护栏段：必须独立目录（.smoke/ 下），不能嵌在 TESTDATA 里（normal 根会抢注）。
TAVERN_CR = os.path.abspath(os.path.join(".smoke", "酒馆源-新建"))
os.makedirs(TAVERN_CR, exist_ok=True)
call("POST", "/api/roots", {"path": TAVERN_CR, "source": "tavernST"})
code = call_code("POST", "/api/items/create", {"kind": "text", "name": "冒烟新建酒馆根", "root": TAVERN_CR})
r = create_one("text", "冒烟新建酒馆根", root=TAVERN_CR)
check("酒馆来源 root 400", code == 400 and "error" in (r or {}), f"HTTP {code}")
# 清理：移除根 + 删空目录
call("DELETE", "/api/roots", {"path": TAVERN_CR})
shutil.rmtree(TAVERN_CR, ignore_errors=True)

# 段末清理：全部新建条目进回收站 → 重扫
for rid in created_ids:
    call("POST", f"/api/items/{rid}/delete", {})
call("POST", "/api/rescan")
check("新建文件段已清理", all(
    call("GET", f"/api/items/{rid}") == {"error": "条目不存在"} for rid in created_ids))
leftover = [fn for fn in os.listdir(TESTDATA) if fn.startswith("冒烟新建")]
check("新建文件磁盘已清理", leftover == [], str(leftover))

print("== 数据目录与修改历史（v0.7.1）==")
# 此前酒馆段把 autoBackup 关了又已恢复 true；先做一次会留备份记录的编辑，保证历史有本条目
r = call("PUT", f"/api/cards/{png_id}", {"fields": {}})
check("history 前置编辑成功", (r or {}).get("ok") is True, str(r)[:60])
meta = call("GET", "/api/meta")
check("meta 带 dataDir", isinstance(meta.get("dataDir"), str) and "testdata-server" in meta["dataDir"],
      str(meta.get("dataDir"))[:80])
h = call("GET", "/api/history")
rows = h.get("rows") or []
check("history 列表按时间倒序", isinstance(rows, list) and all(
    rows[i]["lastModified"] >= rows[i + 1]["lastModified"] for i in range(len(rows) - 1)),
    f"{len(rows)} 行")
top = next((r for r in rows if r["fileName"] == "图像卡.png"), None)
check("history 记录本轮写过的图像卡", top is not None, str([r["fileName"] for r in rows[:5]]))
check("history 行含 kind/edits/rootSource",
      top is not None and top["kind"] == "character" and top["edits"] >= 1 and top["rootSource"] == 0,
      str(top)[:140])
check("history 条目 id 可直达详情", top is not None and call("GET", f"/api/items/{top['id']}")["fileName"] == "图像卡.png")
# 注意：reveal（含 dataDir）会真的在桌面弹出资源管理器窗口——有桌面副作用，不做真实调用的冒烟断言，
# 仅验证未匹配条目时的 404 合同（不会打开任何窗口）
code = call_code("POST", "/api/reveal", {"id": "unknownid0000"})
check("reveal 未知条目 404", code == 404, f"HTTP {code}")

# v0.7.2 审计补缺：text saveas 正向（此前只有错 kind 的 400 负向）
tmp_text = os.path.join(TESTDATA, "冒烟审计文本.txt")
with open(tmp_text, "w", encoding="utf-8") as f:
    f.write("审计夹具")
call("POST", "/api/rescan")
audit_items = call("GET", "/api/items?kind=text&q=" + urllib.parse.quote("冒烟审计文本"))
check("审计文本夹具就绪", len(audit_items) == 1, str([i["fileName"] for i in audit_items]))
r = call("POST", f"/api/text/{audit_items[0]['id']}/saveas", {"content": "另存内容"})
check("text saveas 正向", (r or {}).get("ok") is True and "-副本" in (r or {}).get("fileName", ""), str(r)[:80])
saveas_items = call("GET", "/api/items?kind=text&q=" + urllib.parse.quote("冒烟审计文本-副本"))
check("text saveas 副本入库", len(saveas_items) == 1 and saveas_items[0]["kind"] == "text",
      str([i["fileName"] for i in saveas_items]))

# v0.7.2 审计补缺：history 过滤已删除文件（原文件不存在 → 不再出现）
r = call("PUT", f"/api/text/{audit_items[0]['id']}", {"content": "产生一条备份"})
check("审计文本产生备份", (r or {}).get("ok") is True, str(r)[:60])
rows_before = call("GET", "/api/history").get("rows") or []
check("history 含审计文本", any(x["fileName"] == "冒烟审计文本.txt" for x in rows_before))
for row in (audit_items + saveas_items):
    call("POST", f"/api/items/{row['id']}/delete", {})
rows_after = call("GET", "/api/history").get("rows") or []
check("history 过滤已删除文件", not any(x["fileName"].startswith("冒烟审计文本") for x in rows_after),
      str([x["fileName"] for x in rows_after[:5]]))

print("== 文件监视自动重扫（v0.7.2）==")
# 直接落盘/删除文件，不调 /api/rescan——VaultWatcher 防抖（800ms）后应自动增删索引。
# 这是「外部/酒馆侧改动自动可见」的核心验收；轮询上限 8s 与防抖+重扫耗时无关机器快慢。
auto_path = os.path.join(TESTDATA, "冒烟自动重扫.json")
if os.path.exists(auto_path):
    os.remove(auto_path)
with open(auto_path, "w", encoding="utf-8") as f:
    json.dump({"name": "冒烟自动重扫", "content": "watcher"}, f, ensure_ascii=False)
deadline = time.time() + 8
found = []
while time.time() < deadline:
    found = call("GET", "/api/items?q=" + urllib.parse.quote("冒烟自动重扫"))
    if len(found) == 1:
        break
    time.sleep(0.4)
check("新文件自动入库（免手动重扫）", len(found) == 1 and found[0]["kind"] == "script",
      str([(i["fileName"], i["kind"]) for i in found]))
os.remove(auto_path)
deadline = time.time() + 8
gone = None
while time.time() < deadline:
    gone = call("GET", "/api/items?q=" + urllib.parse.quote("冒烟自动重扫"))
    if not gone:
        break
    time.sleep(0.4)
check("删除后自动出库", gone == [], str(gone)[:80])

print("== 收纳入库（v0.7.3）==")
# 散乱来源夹具（.smoke 下、不是库根——收纳来源无需登记）：混合类型 + 子目录嵌套 + 建议跳过项
COLLECT_SRC = os.path.abspath(os.path.join(".smoke", "收纳来源"))
shutil.rmtree(COLLECT_SRC, ignore_errors=True)


def collect_write(rel, text):
    p = os.path.join(COLLECT_SRC, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        f.write(text)


collect_write("卡A.json", json.dumps({"spec": "chara_card_v2", "spec_version": "2.0",
                                      "data": {"name": "收纳卡A", "description": ""}}, ensure_ascii=False))
collect_write("深层/卡B.json", json.dumps({"spec": "chara_card_v2", "spec_version": "2.0",
                                           "data": {"name": "收纳卡B", "description": ""}}, ensure_ascii=False))
collect_write("书A.json", json.dumps({"entries": {}}, ensure_ascii=False))
collect_write("预设A.json", json.dumps({"name": "P", "prompts": [{"identifier": "main"}],
                                        "prompt_order": []}, ensure_ascii=False))
collect_write("美化A.json", json.dumps({"main_text_color": "rgba(0,0,0,1)", "blur_strength": 1}, ensure_ascii=False))
collect_write("脚本A.js", "console.log(1)")
collect_write("说明.txt", "hello")
collect_write("归档.zip", "PK")

preview = call("POST", "/api/collect/preview", {"source": COLLECT_SRC})
groups = {g["kind"]: g for g in (preview or {}).get("groups", [])}
check("预扫描六类齐全", set(groups) == {"character", "lorebook", "preset", "theme", "script", "text"}
      and len(groups["character"]["files"]) == 2,
      str({k: len(v["files"]) for k, v in groups.items()}))
check("嵌套相对路径保留", any(f["path"].startswith("深层") for f in groups["character"]["files"]),
      str(groups["character"]["files"]))
check("建议跳过归档", any(s["name"] == "归档.zip" for s in (preview or {}).get("skipped", [])),
      str((preview or {}).get("skipped")))

r = call("POST", "/api/collect", {"source": COLLECT_SRC, "root": TESTDATA})
check("收纳执行 ok（7 个可收纳）", (r or {}).get("ok") is True and (r or {}).get("copied") == 7, str(r)[:100])
check("分类落位：角色卡（含嵌套）", os.path.isfile(os.path.join(TESTDATA, "角色卡", "卡A.json"))
      and os.path.isfile(os.path.join(TESTDATA, "角色卡", "卡B.json")))
check("分类落位：其余类型", all(os.path.isfile(os.path.join(TESTDATA, d, f)) for d, f in (
    ("世界书", "书A.json"), ("预设", "预设A.json"), ("美化", "美化A.json"),
    ("脚本", "脚本A.js"), ("文本", "说明.txt"))))
check("源目录默认不动", os.path.isfile(os.path.join(COLLECT_SRC, "卡A.json")))
check("收录条目已入库", call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("收纳卡A")) != [])

r2 = call("POST", "/api/collect", {"source": COLLECT_SRC, "root": TESTDATA})
check("重名自动加序号", (r2 or {}).get("ok") is True
      and os.path.isfile(os.path.join(TESTDATA, "角色卡", "卡A (2).json")), str(r2)[:80])

collect_write("移动我.txt", "move-me")
r3 = call("POST", "/api/collect", {"source": COLLECT_SRC, "root": TESTDATA, "move": True,
                                   "files": ["移动我.txt"]})
check("move 模式源文件已删（回收站）", (r3 or {}).get("ok") is True
      and not os.path.isfile(os.path.join(COLLECT_SRC, "移动我.txt"))
      and os.path.isfile(os.path.join(TESTDATA, "文本", "移动我.txt")), str(r3)[:100])

code = call_code("POST", "/api/collect", {"source": COLLECT_SRC, "root": TESTDATA, "files": ["不存在.txt"]})
check("未知文件清单 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/collect", {"source": COLLECT_SRC, "root": "D:\\不存在的根\\nope"})
check("未登记目标根 400", code == 400, f"HTTP {code}")
code = call_code("POST", "/api/collect/preview", {"source": os.path.join(COLLECT_SRC, "不存在")})
check("来源不存在 400", code == 400, f"HTTP {code}")
TAVERN_COLLECT = os.path.abspath(os.path.join(".smoke", "收纳酒馆源"))
os.makedirs(TAVERN_COLLECT, exist_ok=True)
call("POST", "/api/roots", {"path": TAVERN_COLLECT, "source": "tavernST"})
code = call_code("POST", "/api/collect", {"source": COLLECT_SRC, "root": TAVERN_COLLECT})
check("酒馆源目标 400（只读托管）", code == 400, f"HTTP {code}")
call("DELETE", "/api/roots", {"path": TAVERN_COLLECT})

# 清理：收纳副本进回收站（按目录精确定位），来源目录整体移除
for d in ("角色卡", "世界书", "预设", "美化", "脚本", "文本"):
    for row in call("GET", "/api/items?dir=" + urllib.parse.quote(d)):
        if row.get("fullPath", "").startswith(os.path.abspath(TESTDATA)):
            call("POST", f"/api/items/{row['id']}/delete", {})
shutil.rmtree(COLLECT_SRC, ignore_errors=True)
call("POST", "/api/rescan")
check("收纳段已清理", not any(i["fileName"].startswith(
    ("卡A", "卡B", "书A", "预设A", "美化A", "脚本A", "说明", "移动我"))
    for i in call("GET", "/api/items")))

print("== 错误合同（v0.5.2 回归）==")
code = call_code("GET", "/api/items/unknownid0000")
check("未知条目 id 404", code == 404, f"HTTP {code}")
code = call_code("GET", "/api/thumb/unknownid0000")
check("未知缩略图 id 404", code == 404, f"HTTP {code}")

lore_items = call("GET", "/api/items?kind=lorebook")  # 此刻仅剩测试书（酒馆书已在上一段清理）
code = call_code("POST", f"/api/cards/{lore_items[0]['id']}/saveas", {})
check("错 kind 的 saveas 400", code == 400, f"HTTP {code}")

code = call_code("POST", "/api/roots", {"path": ""})
check("空路径注册根 400", code == 400, f"HTTP {code}")

# cards 端点的 409（T4：此前只测过 text PUT）。外部把文件 mtime 拨早 1 小时——
# 只动时间戳不追加内容（不损伤 PNG），差值远超 1s 容差，断言与机器快慢无关
conflict_items = call("GET", "/api/items?kind=character&q=" + urllib.parse.quote("图像卡"))
check("409 夹具就绪", len(conflict_items) == 1 and conflict_items[0]["fileName"] == "图像卡.png",
      str([i["fileName"] for i in conflict_items]))
conflict_item = call("GET", f"/api/items/{conflict_items[0]['id']}")
stale = conflict_item["modifiedAt"]
t_old = time.time() - 3600
os.utime(conflict_item["fullPath"], (t_old, t_old))
time.sleep(1.5)
body = {"fields": {"description": "不应写入"}, "expectedModified": stale}
code = call_code("PUT", f"/api/cards/{conflict_item['id']}", body)
r = call("PUT", f"/api/cards/{conflict_item['id']}", body)
check("cards PUT 过期 modified 409", code == 409 and "已被外部" in ((r or {}).get("error") or ""), f"HTTP {code}")
card_now = call("GET", f"/api/cards/{conflict_item['id']}")["card"]["data"]
check("409 未写入文件", card_now["description"] == "图像卡描述", card_now["description"])

code = call_code("GET", f"/api/text/{conflict_item['id']}")  # PNG 非文本扩展名
r = call("GET", f"/api/text/{conflict_item['id']}")
check("非文本扩展名 400", code == 400 and (r or {}).get("error") == "该文件类型不支持文本编辑", f"HTTP {code}")

print(f"\n结果：{ok_count} 通过，{fail_count} 失败")
sys.exit(1 if fail_count else 0)
