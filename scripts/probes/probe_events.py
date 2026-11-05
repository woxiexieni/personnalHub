"""
事件卡（R7「交给 AI 整理」）的端到端探针。

为什么需要它：这条链路失败时，客户端只会显示一句"调用失败"，而背后的原因可能是
完全不同的四件事，且**全都不是编译期能发现的**：

  1. 服务端跑的是旧 exe —— 新 rpc 根本没注册，gRPC 返回 UNIMPLEMENTED。
     2026-11-04 就踩过：proto 改了、代码也写了，但没重新编译，server-run.bat
     看 exe 还在就直接启动旧二进制。
  2. ai.json 没配 key —— 服务端返回"没配置 AI"。
  3. key 有效但模型把 token 全花在思维链上，content 是空的（§31 的 deepseek-flash）。
  4. 模型返回了 JSON，但 tag / emotion 不在词表里 —— 校验严格，整张卡被丢掉，
     用户看到的是"这次没整理出事件卡（返回不合规）"，像是 AI 没干活。

这个脚本把 1、2 分开报（它们要的处置完全不同：重编译 vs 填 key），
并对 3、4 断言：有卡、且每个字段都在词表内。

跑法（先 scripts\\server-run.bat 把服务端起在 5000）：
    C:/Users/Lenovo/.workbuddy/binaries/python/envs/default/Scripts/python.exe scripts/probes/probe_events.py

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

# 与客户端 RecordTags.All / RecordDothingPage.Emotions 逐条一致
TAGS = ["工作", "人际", "技术学习", "情绪内耗", "生活", "其他"]
EMOTIONS = ["平静", "焦虑", "烦躁", "激动", "委屈", "兴奋", "失落"]

RAW = (
    "今天下午本来想写一会儿自己的项目，结果临时被叫去开会，"
    "开到五点还没结束。回来越想越烦，觉得自己一天什么都没做成。"
    "晚上本来想补上，但打开电脑就开始刷视频，刷到十一点才反应过来。"
)

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


def main():
    req = pb.ExtractEventsRequest(raw_text=RAW)
    req.tag_options.extend(TAGS)
    req.emotion_options.extend(EMOTIONS)

    print(f"服务端 {ADDR}")
    with grpc.insecure_channel(ADDR) as ch:
        stub = pbg.AiServiceStub(ch)

        try:
            resp = stub.ExtractEvents(req, timeout=120)
        except grpc.RpcError as e:
            code = e.code()
            print(f"\n  [FAIL] 调用失败：{code} - {e.details()}")
            if code == grpc.StatusCode.UNIMPLEMENTED:
                print("\n  这个错误几乎只有一个原因：服务端跑的是旧 exe（没重新编译）。")
                print("  处置：关掉服务端 → scripts\\server-run.bat（它会先 build 再 run）。")
                print("  排查：grep -a -c ExtractEvents PersonalHub.Server/bin/Debug/net8.0/PersonalHub.Server.dll")
                print("        结果应该是 2，如果是 0 就是没编译进去。")
            return 1

    if not resp.ok:
        msg = resp.error_msg
        print(f"\n  [FAIL] 服务端说没法整理：{msg}")
        if "没配置" in msg or "apiKey" in msg:
            print("  处置：填 ai.json 里的 apiKey。")
        elif "不合规" in msg:
            print("  多半是模型没按词表给 tag/emotion，或者 token 被思维链吃光（§31）。")
            print("  可以把 ai.json 的 model 换一个再试，或调大 max_tokens。")
        return 1

    events = list(resp.events)
    check("返回 ok", True)
    check("至少一张卡", len(events) > 0, f"实际 {len(events)} 张")
    if not events:
        return 1

    for e in events:
        check(f"[{e.event_index}] 有事实原文", bool(e.facts and e.facts.strip()))
        check(f"[{e.event_index}] tag 在词表内", e.tag in TAGS, repr(e.tag))
        check(f"[{e.event_index}] emotion 在词表内", e.emotion in EMOTIONS, repr(e.emotion))
        check(f"[{e.event_index}] 有标题", bool((e.title or "").strip()))

    print()
    for e in events:
        print(f"  [{e.event_index}] {e.title}")
        print(f"       tag={e.tag}  emotion={e.emotion}")
        print(f"       facts={e.facts[:70]}")
    return 0


if __name__ == "__main__":
    rc = main()
    print(f"\n通过 {passed} / 失败 {failed}")
    sys.exit(1 if failed else rc)
