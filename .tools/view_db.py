# -*- coding: utf-8 -*-
"""
PersonalHub 服务端账本查看器（只读，不改任何数据）
用法：  python view_db.py
"""
import os
import sqlite3
from datetime import datetime, timedelta, timezone

def _resolve_db():
    # 1) 环境变量优先（与服务端 PERSONALHUB_DB 保持一致）
    custom = os.environ.get("PERSONALHUB_DB")
    if custom:
        return custom
    # 2) 仓库根目录（本脚本位于 <repo>\.tools\ 下，上一级即仓库根）
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    primary = os.path.join(repo_root, "personalhub.db3")
    if os.path.exists(primary):
        return primary
    # 3) 兼容旧位置
    legacy = os.path.join(os.environ.get("LOCALAPPDATA", ""), "PersonalHub", "personalhub.db3")
    if os.path.exists(legacy):
        return legacy
    # 都没有：返回新的期望位置（下面会提示还不存在）
    return primary


DB = _resolve_db()

CATEGORY = {1: "餐饮", 2: "交通", 3: "购物", 4: "居住", 5: "娱乐", 6: "医疗", 7: "其他"}


def ts2local(ms):
    """Unix 毫秒 -> 本地时间字符串"""
    if ms is None:
        return "-"
    try:
        return datetime.fromtimestamp(ms / 1000.0).strftime("%Y-%m-%d %H:%M")
    except (OSError, ValueError, OverflowError):
        return str(ms)


def main():
    print("=" * 78)
    print(" PersonalHub 服务端账本")
    print(" 库文件:", DB)
    print("=" * 78)

    if not os.path.exists(DB):
        print("\n[!] 库文件还不存在。")
        print("    说明服务端还没成功写入过数据。先启动一次服务端、同步几条记录即可生成。")
        return

    con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    cur = con.cursor()

    total = cur.execute("SELECT COUNT(*) FROM records").fetchone()[0]
    if total == 0:
        print("\n[空] 表 records 里还没有任何记录。")
        print("    手机连上同局域网同步成功后，这里就会出现数据。")
        con.close()
        return

    rows = cur.execute(
        "SELECT id, amount, category_id, note, spent_at, pay_method, created_at "
        "FROM records ORDER BY spent_at DESC"
    ).fetchall()
    con.close()

    print(f"\n共 {total} 条记录（按消费时间倒序）\n")
    head = f"{'消费时间':<17}{'金额':>9}  {'分类':<6}{'支付':<8}{'备注':<14}{'id'}"
    print(head)
    print("-" * 78)

    day_total = {}
    for rid, amount, cat, note, spent_at, pay, created in rows:
        d = datetime.fromtimestamp((spent_at or 0) / 1000.0).strftime("%Y-%m-%d")
        day_total[d] = day_total.get(d, 0.0) + (amount or 0.0)
        note = (note or "").replace("\n", " ")
        if len(note) > 12:
            note = note[:12] + ".."
        print(
            f"{ts2local(spent_at):<17}{amount:>9.2f}  "
            f"{CATEGORY.get(cat, str(cat)):<6}{(pay or '-'):<8}{note:<14}{rid}"
        )

    print("-" * 78)
    print("\n按天汇总：")
    grand = 0.0
    for d in sorted(day_total, reverse=True):
        print(f"  {d}   合计 {day_total[d]:>9.2f}")
        grand += day_total[d]
    print(f"  {'总计':<10} 合计 {grand:>9.2f}")

    dates = sorted(day_total)
    print(f"\n覆盖 {len(dates)} 天，日均 {grand / len(dates):.2f}")
    print("=" * 78)


if __name__ == "__main__":
    main()
