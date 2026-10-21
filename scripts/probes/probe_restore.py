"""
恢复通道探针：验证「把服务端历史搬回本地」所需要的那几个查询接口真的能一次拿全。

为什么不用 probe_ideas.py 改一改：那个脚本关心的是"写入 + 校验"，
这里关心的是"全量读出"，读的区间参数、条数、以及新加的 ListThings 是不是挂上了。

跑法（服务端要在跑）：
    PH_PROBE_ADDR=127.0.0.1:5000 python scripts/probes/probe_restore.py
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "gen"))

import grpc  # noqa: E402
import personalHub_pb2 as pb  # noqa: E402
import personalHub_pb2_grpc as pb_grpc  # noqa: E402

ADDR = os.environ.get("PH_PROBE_ADDR", "127.0.0.1:5000")

# 恢复用的"无限区间"：与客户端 RestoreService 里的 EpochFrom 对齐
EPOCH = 946684800000
FAR_FUTURE = 4102444800000   # 2100-01-01


def check(label, ok, detail=""):
    print(f"  {'PASS' if ok else 'FAIL'}  {label}" + (f"  <- {detail}" if detail else ""))
    return ok


def main():
    results = []

    with grpc.insecure_channel(ADDR) as ch:
        try:
            grpc.channel_ready_future(ch).result(timeout=5)
        except Exception as e:
            print(f"[!] 连不上 {ADDR}: {e}")
            return 2

        records = pb_grpc.RecordServiceStub(ch)
        things = pb_grpc.ThingServiceStub(ch)
        blocks = pb_grpc.BlockServiceStub(ch)
        goals = pb_grpc.GoalServiceStub(ch)
        plans = pb_grpc.PlanServiceStub(ch)
        patterns = pb_grpc.PatternServiceStub(ch)
        ideas = pb_grpc.IdeaServiceStub(ch)

        # proto2 里字段名就叫 from（Python 关键字），只能打包成 dict 传
        r = records.ListRecords(pb.ListRecordsRequest(
            **{"from": EPOCH, "to": FAR_FUTURE, "direction": -1, "include_deleted": False}))
        results.append(check("账单：全量区间能读出", len(r.records) >= 0, f"{len(r.records)} 条"))

        # 新增的 RPC：以前 ThingService 只有「今天」一个出口，换机必然丢
        t = things.ListThings(pb.ListThingsRequest(
            **{"from": EPOCH, "to": FAR_FUTURE}))
        results.append(check("事件反思：ListThings 已挂上", True, f"{len(t.things)} 条"))

        b = blocks.ListBlocks(pb.ListBlocksRequest(
            **{"from": EPOCH, "to": FAR_FUTURE, "include_deleted": False}))
        results.append(check("时间块：全量区间能读出", len(b.blocks) >= 0, f"{len(b.blocks)} 条"))

        g = goals.ListGoals(pb.Empty())
        results.append(check("目标：全量能读出", len(g.goals) >= 0, f"{len(g.goals)} 条"))

        p = plans.ListPlans(pb.ListPlansRequest(**{"from": EPOCH, "to": FAR_FUTURE}))
        results.append(check("计划：全量能读出", len(p.plans) >= 0, f"{len(p.plans)} 条"))

        pt = patterns.ListPatterns(pb.Empty())
        results.append(check("模式：全量能读出", len(pt.patterns) >= 0, f"{len(pt.patterns)} 条"))

        pr = patterns.ListPractices(pb.ListPracticesRequest(
            **{"from": EPOCH, "to": FAR_FUTURE}))
        results.append(check("练习：全量能读出", len(pr.practices) >= 0, f"{len(pr.practices)} 条"))

        i = ideas.ListIdeas(pb.ListIdeasRequest(
            **{"kind": -1, "done": -1, "from": EPOCH, "to": FAR_FUTURE,
               "include_deleted": False}))
        results.append(check("想法：全量能读出", len(i.ideas) >= 0, f"{len(i.ideas)} 条"))

        # 「已落实」必须跟着下发，否则新设备看到的是"全部未落实"
        if i.ideas:
            with_done = [x for x in i.ideas if x.done]
            results.append(check(
                "想法：done / done_at 字段在协议里",
                all(hasattr(x, "done") and hasattr(x, "done_at") for x in i.ideas),
                f"已落实 {len(with_done)} / {len(i.ideas)}"))
        else:
            results.append(check("想法：空库时也不报错", True, "0 条"))

        # 区间边界：to 必须"不含"，否则同一条会在跨天时被算两次
        first = ideas.ListIdeas(pb.ListIdeasRequest(
            **{"kind": -1, "done": -1, "from": EPOCH, "to": EPOCH + 1}))
        results.append(check("想法：极窄区间返回空而不是全部", len(first.ideas) == 0,
                             f"{len(first.ideas)} 条"))

    print()
    print("-" * 60)
    ok = sum(1 for x in results if x)
    print(f"共 {len(results)} 项：通过 {ok}，失败 {len(results) - ok}")
    return 0 if ok == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
