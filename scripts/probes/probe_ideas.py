"""
IdeaService（R6 想法）的端到端探针。

为什么要有它：gRPC 那条链路里最容易出错的不是"方法调不通"（那是编译期就发现的），
而是**语义**：校验有没有真的拒绝、版本冲突有没有真的算失败、软删有没有真的生效。
这些东西编译能过、界面也点得动，但数据是错的 —— 必须打真的服务端验证。

跑法（先用 scripts/server-run.bat 把服务端起在 5000）：
    C:/Users/Lenovo/.workbuddy/binaries/python/envs/default/Scripts/python.exe scripts/probes/probe_ideas.py

注意：本机会设 HTTP_PROXY，gRPC 走代理会报 "unable to establish HTTP/2 connection"，
脚本里已经把 NO_PROXY 设好了。
"""
import os
import sys
import time

os.environ["HTTP_PROXY"] = ""
os.environ["HTTPS_PROXY"] = ""
os.environ["NO_PROXY"] = "127.0.0.1,localhost"

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "gen"))

import grpc  # noqa: E402
import personalHub_pb2 as pb  # noqa: E402
import personalHub_pb2_grpc as pbg  # noqa: E402

ADDR = "127.0.0.1:5000"
STAMP = str(int(time.time()))
RESULTS = []


def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (("  <- " + detail) if detail else ""))


def upload(stub, ideas):
    req = pb.UploadIdeasRequest()
    req.ideas.extend(ideas)
    return stub.UploadIdeas(req)


def list_ideas(stub, kind=-1, include_deleted=False):
    return stub.ListIdeas(pb.ListIdeasRequest(kind=kind, include_deleted=include_deleted))


def main():
    ch = grpc.insecure_channel(ADDR)
    stub = pbg.IdeaServiceStub(ch)

    # ---------- 0. 服务端可达 ----------
    try:
        grpc.channel_ready_future(ch).result(timeout=5)
        check("服务端可达", True, ADDR)
    except Exception as e:
        check("服务端可达", False, str(e))
        return report()

    now = int(time.time() * 1000)
    id_a = "probe-idea-a-" + STAMP
    id_b = "probe-idea-b-" + STAMP

    # ---------- 1. 正常写入 ----------
    r = upload(stub, [
        pb.Idea(id=id_a, text="今天开了 3 小时会，只决定了 2 件事",
                kind=1, created_at=now, updated_at=now),
        pb.Idea(id=id_b, text="我真笨，又忘了回消息",
                kind=2, created_at=now, updated_at=now),
    ])
    check("上传两条都收下", r.accepted_count == 2 and len(r.failed_ids) == 0,
          f"accepted={r.accepted_count} failed={list(r.failed_ids)}")

    # ---------- 2. 空正文必须被拒 ----------
    r = upload(stub, [pb.Idea(id="probe-empty-" + STAMP, text="   ",
                              kind=0, created_at=now, updated_at=now)])
    check("空正文被拒", r.accepted_count == 0 and len(r.failed_ids) == 1,
          f"accepted={r.accepted_count} err={r.error_msg}")

    # ---------- 3. 非法 kind 必须被拒 ----------
    r = upload(stub, [pb.Idea(id="probe-kind-" + STAMP, text="类型越界测试",
                              kind=9, created_at=now, updated_at=now)])
    check("kind=9 被拒", r.accepted_count == 0 and len(r.failed_ids) == 1,
          f"accepted={r.accepted_count} err={r.error_msg}")

    # ---------- 4. 版本冲突必须算失败，且文案带 conflict ----------
    # 客户端靠 ErrorMsg 里有没有 "conflict" 决定"不重试、拉服务端覆盖"，
    # 所以这句文案是协议的一部分，不是给人看的装饰。
    r = upload(stub, [pb.Idea(id=id_a, text="旧版本的标题",
                              kind=1, created_at=now, updated_at=now - 60000)])
    conflict_ok = (r.accepted_count == 0
                   and len(r.failed_ids) == 1
                   and "conflict" in (r.error_msg or "").lower())
    check("旧时间戳被算冲突且文案含 conflict", conflict_ok,
          f"accepted={r.accepted_count} err={r.error_msg}")

    # ---------- 5. 新时间戳可以覆盖 ----------
    r = upload(stub, [pb.Idea(id=id_a, text="今天开了 3 小时会，只决定了 2 件事（已改）",
                              kind=1, created_at=now, updated_at=now + 1000)])
    check("新时间戳可以覆盖", r.accepted_count == 1 and len(r.failed_ids) == 0,
          f"accepted={r.accepted_count} err={r.error_msg}")

    # ---------- 6. 读取：条数 / 筛选 / 内容 ----------
    all_list = list_ideas(stub)
    mine = [x for x in all_list.ideas if x.id in (id_a, id_b)]
    check("ListIdeas 能读回两条", len(mine) == 2, f"读到 {len(mine)} 条")

    updated = [x for x in mine if x.id == id_a]
    check("覆盖后的正文是最新版",
          bool(updated) and updated[0].text.endswith("（已改）"),
          updated[0].text if updated else "没读到")

    kind2 = list_ideas(stub, kind=2)
    check("按 kind=2 筛选只返回评价类",
          all(x.kind == 2 for x in kind2.ideas) and any(x.id == id_b for x in kind2.ideas),
          f"返回 {len(kind2.ideas)} 条")

    # ---------- 7. 软删 ----------
    r = upload(stub, [pb.Idea(id=id_b, text="我真笨，又忘了回消息",
                              kind=2, created_at=now, updated_at=now + 2000, deleted=True)])
    after = [x for x in list_ideas(stub).ideas if x.id == id_b]
    check("删除后不再出现在默认列表", r.accepted_count == 1 and len(after) == 0,
          f"accepted={r.accepted_count} 仍可见={len(after)}")

    # 墓碑必须真的还在库里（deleted=1），而不是被物理删掉：
    # 物理删会让"手机上还留着的旧副本"下次同步时把这条记录重新灌回来。
    tomb = [x for x in list_ideas(stub, include_deleted=True).ideas if x.id == id_b]
    check("墓碑仍在库（include_deleted 查得到且 deleted=True）",
          len(tomb) == 1 and tomb[0].deleted,
          f"查到 {len(tomb)} 条")

    return report()


def report():
    print()
    total = len(RESULTS)
    passed = sum(1 for _, ok, _ in RESULTS if ok)
    print(f"===== {passed}/{total} 通过 =====")
    for name, ok, detail in RESULTS:
        if not ok:
            print(f"  未通过: {name}  {detail}")
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
