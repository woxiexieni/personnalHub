"""
目标 30 天归纳（CommentGoal）的端到端探针。

这条链路和事件卡那条一样，失败时客户端只会显示一句"没接上"，
背后的原因可能是完全不同的四件事，全都不是编译期能发现的：

  1. 服务端跑的是旧 exe —— 新 rpc 没注册，返回 UNIMPLEMENTED（2026-11-04 踩过）
  2. ai.json 没配 key
  3. key 有效但思维链把 token 吃光，content 是空的（deepseek-flash，§31）
  4. **模型给了建议** —— 这是本探针最在意的一条。

第 4 条为什么单独断言：这一块的唯一价值是"只归纳，不建议"。
模型天生爱给方向，提示词只是软约束。一旦它开始写"你应该…"，
这个功能就退化成一张建议清单 —— 正是反 Goodhart 要避免的东西，
而且用户看一眼就会失去信任，再也不会点第二次。

跑法（先 scripts\\server-run.bat 把服务端起在 5000）：
    C:/Users/Lenovo/.workbuddy/binaries/python/envs/default/Scripts/python.exe scripts/probes/probe_goal.py
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

# 一出现就说明它在给建议，而不是在归纳。宁可误杀，也不放过去。
#
# 刻意不含「值得」：它是系统自己的评价标签（Worth=1 就叫"值得"），
# 引用「值得」那几句是合法归纳。第一版把它列进来，结果稳定误杀。
BANNED = [
    "应该", "建议", "不如", "可以试", "试试", "下次要", "下次可以",
    "加油", "坚持", "一定要", "需要你", "最好", "务必",
]

passed = 0
failed = 0


def check(name, cond, detail=""):
    global passed, failed
    if cond:
        print(f"  [OK]   {name}")
        passed += 1
    else:
        print(f"  [FAIL] {name}" + (f"  -> {detail}" if detail else ""))
        failed += 1


def build_facts():
    return pb.GoalFacts(
        goal_title="把 PersonalHub 的时间记录真正用起来",
        days=30,
        blocks=13,
        minutes=640,
        worth_good=3,
        worth_mid=7,
        worth_bad=3,
        top_piece="写代码",
        lessons=[
            "先写十分钟再说，写着写着就进去了",
            "先动手写十行，比想清楚再写快",
            "把要做的那一步写在纸上，再开电脑",
        ],
        bad_notes=[
            "调那个接口，一直在绕过原设计",
            "又在这个接口上返工",
            "同一个地方第三次改",
        ],
    )


def main():
    print(f"服务端 {ADDR}")
    with grpc.insecure_channel(ADDR) as ch:
        stub = pbg.AiServiceStub(ch)

        try:
            resp = stub.CommentGoal(build_facts(), timeout=120)
        except grpc.RpcError as e:
            code = e.code()
            print(f"\n  [FAIL] 调用失败：{code} - {e.details()}")
            if code == grpc.StatusCode.UNIMPLEMENTED:
                print("\n  这个错误几乎只有一个原因：服务端跑的是旧 exe（没重新编译）。")
                print("  处置：关掉服务端 → scripts\\server-run.bat（它会先 build 再 run）。")
                print("  排查：grep -a -c CommentGoal "
                      "PersonalHub.Server/bin/Debug/net8.0/PersonalHub.Server.dll")
                print("        结果应该是 2，如果是 0 就是没编译进去。")
            return 1

    if not resp.ok:
        msg = resp.error_msg
        print(f"\n  [FAIL] 服务端没给结论：{msg}")
        if "没配置" in msg or "apiKey" in msg:
            print("  处置：填 ai.json 里的 apiKey。")
        elif "没看出重复" in msg:
            print("  模型判断这批材料里没有重复 —— 换一组材料再试，不一定是 bug。")
        return 1

    lines = list(resp.lines)
    check("返回 ok", True)
    check("2 到 4 行", 2 <= len(lines) <= 4, f"实际 {len(lines)} 行")

    for i, line in enumerate(lines):
        check(f"[{i}] 非空", bool(line.strip()))
        check(f"[{i}] 不超过 60 字", len(line) <= 60, f"{len(line)} 字：{line}")
        hits = [w for w in BANNED if w in line]
        check(f"[{i}] 没在给建议", not hits, f"命中 {hits}：{line}")

    print()
    for line in lines:
        print(f"  · {line}")

    # 数据太少必须闭嘴 —— 这是"说空话就没人点第二次"的唯一软件保障
    print()
    print("--- 少于 3 段时应拒绝回答 ---")
    thin = pb.GoalFacts(goal_title="测试用目标", days=30, blocks=2, minutes=40)
    with grpc.insecure_channel(ADDR) as ch:
        thin_resp = pbg.AiServiceStub(ch).CommentGoal(thin, timeout=60)

    check("少样本时不给结论", not thin_resp.ok)
    check("少样本时说明了原因", "3 段" in (thin_resp.error_msg or ""),
          repr(thin_resp.error_msg))

    print()
    print(f"通过 {passed} / 失败 {failed}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
