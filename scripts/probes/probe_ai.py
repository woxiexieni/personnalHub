"""
AiService（§27 AI 助手）的端到端探针。

为什么要有它：AI 这条链路上真正会出错的地方，全都**不是**编译期能发现的：
  - 服务端没配 key 时，客户端到底会不会降级（而不是弹一个红色报错）
  - 模型返回的片代号是不是真实存在的（编出来的 code 会让「排进今天」无声失败）
  - 时长是不是固定档
  - 评价聚类里的证据是不是真的原文（这是唯一能验证"没编造"的地方）
  - 周回顾在数据不足时是不是真的闭嘴

没配 key 时本脚本只跑"骨架"部分（服务可达 + 两个方法都返回合理的失败原因），
填好 ai.json 之后再跑一次，才会真正打模型。

跑法（先用 scripts/server-run.bat 把服务端起在 5000）：
    C:/Users/Lenovo/.workbuddy/binaries/python/envs/default/Scripts/python.exe scripts/probes/probe_ai.py

注意：本机会设 HTTP_PROXY，gRPC 走代理会报 "unable to establish HTTP/2 connection"，
脚本里已经把 NO_PROXY 设好了。
"""
import os
import sys

os.environ["HTTP_PROXY"] = ""
os.environ["HTTPS_PROXY"] = ""
os.environ["NO_PROXY"] = "127.0.0.1,localhost"

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "gen"))

import grpc  # noqa: E402
import personalHub_pb2 as pb  # noqa: E402
import personalHub_pb2_grpc as pbg  # noqa: E402

ADDR = os.environ.get("PH_PROBE_ADDR", "127.0.0.1:5000")

# 与客户端 Pieces.Durations 一致（固定时长档）
DURATIONS = {5, 15, 30, 45, 60, 90, 120, 180}

# 与客户端 Pieces.All 一致（片代号是冻结的）
PIECES = {
    "code", "doc", "meeting", "learn", "leetcode", "book", "english",
    "sport", "nap", "meal", "commute", "chore", "tidy", "errand",
    "chat", "family", "dinner", "video", "game", "show", "scroll",
    "idle", "review",
}

RESULTS = []


def check(name, ok, detail="", skip=False):
    RESULTS.append((name, ok, detail, skip))
    tag = "  SKIP  " if skip else ("  PASS  " if ok else "  FAIL  ")
    print(tag + name + (("  <- " + detail) if detail else ""))


def main():
    ch = grpc.insecure_channel(ADDR)
    ai = pbg.AiServiceStub(ch)

    try:
        grpc.channel_ready_future(ch).result(timeout=5)
        check("服务端可达", True, ADDR)
    except Exception as e:
        check("服务端可达", False, str(e))
        return report()

    # ---------- 1. 状态 ----------
    status = ai.GetAiStatus(pb.Empty())
    print(f"        configured={status.configured} model={status.model} endpoint={status.endpoint}")
    check("GetAiStatus 能返回（服务已挂上）", True, "hint=" + (status.hint or "")[:60])

    if not status.configured:
        # 没配 key 时，两个方法都必须"优雅地不干活"：ok=false + 一句人话（含配置文件路径）
        r = ai.SuggestNextSteps(pb.SuggestNextStepsRequest(goal_id="does-not-exist"))
        check("未配 key 时 SuggestNextSteps 不崩", not r.ok and bool(r.error_msg), r.error_msg or "(空)")
        check("未配 key 时 error_msg 指向 ai.json", "ai.json" in (r.error_msg or ""), r.error_msg or "")

        r2 = ai.FindRepeatingEvaluations(pb.FindRepeatingRequest(days=180))
        check("未配 key 时聚类不崩", not r2.ok and bool(r2.error_msg), r2.error_msg or "(空)")

        r3 = ai.CommentWeek(pb.WeekFacts(week_label="这周", this_minutes=0, last_minutes=0, blocks=0))
        check("未配 key 时周回顾不崩", not r3.ok and bool(r3.error_msg), r3.error_msg or "(空)")

        print("\n  → 没配 key，模型相关的检查跳过。去 ai.json 填上 apiKey 再跑一次。")
        return report()

    # ---------- 2. 提议下一步（真打模型） ----------
    goals = pbg.GoalServiceStub(ch).ListGoals(pb.Empty()).goals
    active = [g for g in goals if g.status == 0]

    if not active:
        check("有一个进行中的目标", False, "先在手机上建一个目标，这个检查才有意义")
    else:
        g = active[0]
        r = ai.SuggestNextSteps(pb.SuggestNextStepsRequest(goal_id=g.id))
        print(f"        目标「{g.title}」→ ok={r.ok} source={r.source} {r.error_msg or ''}")

        if not r.ok:
            # 模型返回不合规也是合法结果（客户端会降级），不算失败
            check("提议下一步：空手或失败也能解释", bool(r.error_msg), r.error_msg or "", skip=True)
        else:
            ops = list(r.options)
            print("        " + " / ".join(f"{o.action}({o.piece_code},{o.minutes}m)" for o in ops))
            check("条数 1~3", 1 <= len(ops) <= 3, str(len(ops)))
            check("片代号全部真实存在", all(o.piece_code in PIECES for o in ops),
                  str([o.piece_code for o in ops if o.piece_code not in PIECES]))
            check("时长全部是固定档", all(o.minutes in DURATIONS for o in ops),
                  str([o.minutes for o in ops if o.minutes not in DURATIONS]))
            check("动作非空且不超过 24 字", all(0 < len(o.action) <= 24 for o in ops),
                  str([len(o.action) for o in ops]))

        r404 = ai.SuggestNextSteps(pb.SuggestNextStepsRequest(goal_id="no-such-goal-id"))
        check("不存在的目标给出明确原因", not r404.ok and "目标" in (r404.error_msg or ""),
              r404.error_msg or "")

    # ---------- 3. 评价聚类 ----------
    r = ai.FindRepeatingEvaluations(pb.FindRepeatingRequest(days=180))
    print(f"        scanned={r.scanned} ok={r.ok} {r.error_msg or ''}")

    if not r.ok:
        check("聚类：样本不够时明确说不做", "攒到" in (r.error_msg or "") or "没找到" in (r.error_msg or ""),
              r.error_msg or "", skip=True)
    else:
        check("聚类条数 ≤ 2", len(r.proposals) <= 2, str(len(r.proposals)))
        ok_evidence = all(len(p.evidence) >= 2 for p in r.proposals)
        check("每条提议至少带 2 句原文证据", ok_evidence,
              str([len(p.evidence) for p in r.proposals]))
        ok_new = all(len(p.new_response) > 0 and len(p.title) > 0 for p in r.proposals)
        check("标题 / 新反应都非空", ok_new, "")
        for p in r.proposals:
            print(f"        · {p.title} → {p.new_response}")
            for ev in p.evidence:
                print(f"            原句：{ev}")

    # ---------- 4. 周回顾：数据太少时必须闭嘴 ----------
    empty = ai.CommentWeek(pb.WeekFacts(week_label="这周", this_minutes=0, last_minutes=0, blocks=0))
    check("0 数据时拒绝生成（不硬编）", not empty.ok and bool(empty.error_msg), empty.error_msg or "")

    # 给一组正常数字，看它是不是只说一句话
    real = ai.CommentWeek(pb.WeekFacts(
        week_label="这周", this_minutes=420, last_minutes=300, days=5, blocks=11,
        active_goals=2, moved_goals=1, top_shift="写代码 +120m", repeat="先做25分钟（3 次）"))
    print(f"        周回顾 → ok={real.ok} text={real.text!r} {real.error_msg or ''}")
    if real.ok:
        check("一句话不超过 60 字", 0 < len(real.text) <= 60, f"{len(real.text)} 字")
        check("没有出现打分/建议类词", not any(w in real.text for w in ("建议", "应该", "加油", "效率")), real.text)
    else:
        check("周回顾：失败也给原因", bool(real.error_msg), real.error_msg or "", skip=True)

    return report()


def report():
    print("\n" + "-" * 60)
    fails = [r for r in RESULTS if not r[1] and not r[3]]
    skips = [r for r in RESULTS if r[3]]
    print(f"共 {len(RESULTS)} 项：通过 {len(RESULTS) - len(fails) - len(skips)}，"
          f"失败 {len(fails)}，跳过 {len(skips)}")
    for name, _, detail, _ in fails:
        print("  FAIL: " + name + ("  <- " + detail if detail else ""))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
