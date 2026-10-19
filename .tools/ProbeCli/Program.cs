using Grpc.Net.Client;
using Personalhub;

string addr = args.Length > 0 ? args[0] : "http://127.0.0.1:5000";
string mode = args.Length > 1 ? args[1] : "upload";

using var channel = GrpcChannel.ForAddress(addr);
var sys = new Personalhub.System.SystemClient(channel);
var rec = new RecordService.RecordServiceClient(channel);

var ping = await sys.PingAsync(new PingRequest());
Console.WriteLine($"Ping ok={ping.Ok}");

if (mode == "query")
{
    var today = await rec.GetTodayRecordsAsync(new Empty());
    Console.WriteLine($"今日条数={today.Records.Count}");
    foreach (var r in today.Records)
        Console.WriteLine($"   id={r.Id} amount={r.Amount} cat={r.CategoryId} pay={r.PayMethod} note={r.Note}");

    var trend = await rec.GetExpenseTrendAsync(new TrendRequest { Range = "7d" });
    Console.WriteLine($"趋势 total={trend.Total} avg={trend.DailyAvg} points={trend.Points.Count}");
}
else if (mode == "r2")
{
    await RunR2Checks(channel);
}
else if (mode == "r4")
{
    await RunR4Checks(channel);
}
else if (mode == "r5")
{
    await RunR5Checks(channel);
}
else
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var req = new UploadRecordsRequest();
    req.Records.Add(new Record { Id = "probe-a", Amount = 12.5, CategoryId = 1, PayMethod = "现金", Note = "探针A", SpentAt = now, CreatedAt = now });
    req.Records.Add(new Record { Id = "probe-b", Amount = 30.0, CategoryId = 2, PayMethod = "微信", Note = "探针B", SpentAt = now, CreatedAt = now });

    var resp = await rec.UploadRecordsAsync(req);
    Console.WriteLine($"上传 accepted={resp.AcceptedCount} failed=[{string.Join(",", resp.FailedIds)}]");

    var today = await rec.GetTodayRecordsAsync(new Empty());
    Console.WriteLine($"上传后今日条数={today.Records.Count}");
}

// ============================================================
// R2：时间块 + 目标的接口断言
// ============================================================

static async Task RunR2Checks(GrpcChannel channel)
{
    var blocks = new BlockService.BlockServiceClient(channel);
    var goals = new GoalService.GoalServiceClient(channel);

    int pass = 0, fail = 0;

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"  [OK]   {name}"); }
        else { fail++; Console.WriteLine($"  [FAIL] {name}  {detail}"); }
    }

    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    string tag = now.ToString();

    // ---------- 目标 ----------
    var g1 = new Goal
    {
        Id = "g-" + tag,
        Title = "做完 PersonalHub",
        Status = 0,
        NextStep = "把时间块录完",
        IsCurrent = true,
        StartedAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    var gu = await goals.UploadGoalsAsync(new UploadGoalsRequest { Goals = { g1 } });
    Check("新建目标被接受", gu.AcceptedCount == 1, $"accepted={gu.AcceptedCount} err={gu.ErrorMsg}");

    var gl = await goals.ListGoalsAsync(new Empty());
    var saved = gl.Goals.FirstOrDefault(g => g.Id == g1.Id);
    Check("目标能查回来", saved is not null);
    Check("is_current 生效", saved?.IsCurrent == true);
    Check("累计投入初始为 0", saved?.TotalMinutes == 0, $"total={saved?.TotalMinutes}");

    var badGoal = new Goal { Id = "g-bad-" + tag, Title = "   ", StartedAt = now, CreatedAt = now, UpdatedAt = now };
    var gu2 = await goals.UploadGoalsAsync(new UploadGoalsRequest { Goals = { badGoal } });
    Check("空标题目标被拒", gu2.AcceptedCount == 0 && gu2.FailedIds.Count == 1, $"acc={gu2.AcceptedCount}");

    // ---------- 时间块 ----------
    var half = new Block
    {
        Id = "b-half-" + tag,
        PieceCode = "code",
        OccurredAt = now,
        CreatedAt = now,
        UpdatedAt = now,
        GoalId = g1.Id,
    };
    var bu = await blocks.UploadBlocksAsync(new UploadBlocksRequest { Blocks = { half } });
    Check("只有 piece_code 的块被接受（半条记录）", bu.AcceptedCount == 1, $"acc={bu.AcceptedCount} err={bu.ErrorMsg}");

    var noPiece = new Block { Id = "b-nopiece-" + tag, OccurredAt = now, CreatedAt = now, UpdatedAt = now };
    var bu2 = await blocks.UploadBlocksAsync(new UploadBlocksRequest { Blocks = { noPiece } });
    Check("缺事情片被拒", bu2.AcceptedCount == 0, $"acc={bu2.AcceptedCount}");

    var batch = new UploadBlocksRequest();
    batch.Blocks.Add(new Block { Id = "b1-" + tag, PieceCode = "code", OccurredAt = now - 3600_000, DurationMin = 90, GoalId = g1.Id, CreatedAt = now, UpdatedAt = now });
    batch.Blocks.Add(new Block { Id = "b2-" + tag, PieceCode = "code", OccurredAt = now - 7200_000, DurationMin = 45, GoalId = g1.Id, CreatedAt = now, UpdatedAt = now });
    batch.Blocks.Add(new Block { Id = "b3-" + tag, PieceCode = "scroll", OccurredAt = now - 1800_000, DurationMin = 30, CreatedAt = now, UpdatedAt = now });
    batch.Blocks.Add(new Block { Id = "b4-" + tag, PieceCode = "meal", OccurredAt = now - 5400_000, DurationMin = 0, CreatedAt = now, UpdatedAt = now });

    var bu3 = await blocks.UploadBlocksAsync(batch);
    Check("批量上传 4 条全部接受", bu3.AcceptedCount == 4, $"acc={bu3.AcceptedCount} err={bu3.ErrorMsg}");

    await blocks.UploadBlocksAsync(batch);
    var list1 = await blocks.ListBlocksAsync(new ListBlocksRequest { From = now - 86400_000, To = now + 60_000 });
    Check("重复上传幂等（不产生重复行）",
        list1.Blocks.Count(b => b.Id.EndsWith(tag, StringComparison.Ordinal)) == 5,
        $"count={list1.Blocks.Count}");

    // ---------- 统计 ----------
    var st = await blocks.GetPieceStatsAsync(new PieceStatsRequest { Period = "today" });
    var codeSlice = st.Pieces.FirstOrDefault(p => p.PieceCode == "code");
    Check("按片聚合：code 合计 135 分钟", codeSlice?.Minutes == 135, $"minutes={codeSlice?.Minutes}");
    Check("按片聚合：code 条数 3（含半条）", codeSlice?.Count == 3, $"count={codeSlice?.Count}");
    Check("covered = 各片之和", st.CoveredMinutes == st.Pieces.Sum(p => p.Minutes), $"{st.CoveredMinutes}");
    Check("未记录 = 已过时间 - 已记录，且 >= 0",
        st.UnrecordedMinutes >= 0 && Math.Abs(st.UnrecordedMinutes - (st.TotalMinutes - st.CoveredMinutes)) <= 1,
        $"total={st.TotalMinutes} covered={st.CoveredMinutes} unrec={st.UnrecordedMinutes}");
    Check("today 的总分钟数在合理范围（< 1441）", st.TotalMinutes is > 0 and <= 1440, $"{st.TotalMinutes}");

    // ---------- 目标计算值 ----------
    var gl2 = await goals.ListGoalsAsync(new Empty());
    var saved2 = gl2.Goals.FirstOrDefault(g => g.Id == g1.Id);
    Check("目标累计投入 = 135（90+45，半条按 0 计）", saved2?.TotalMinutes == 135, $"total={saved2?.TotalMinutes}");
    Check("目标最后活动时间已回填", (saved2?.LastActivityAt ?? 0) > 0, $"last={saved2?.LastActivityAt}");

    // ---------- 软删 ----------
    var del = await blocks.UploadBlocksAsync(new UploadBlocksRequest
    {
        Blocks = { new Block { Id = "b1-" + tag, Deleted = true, UpdatedAt = now } }
    });
    Check("软删上传被接受", del.AcceptedCount == 1);

    var list2 = await blocks.ListBlocksAsync(new ListBlocksRequest { From = now - 86400_000, To = now + 60_000 });
    Check("默认查询不含已删块", list2.Blocks.All(b => b.Id != "b1-" + tag));

    var list3 = await blocks.ListBlocksAsync(new ListBlocksRequest { From = now - 86400_000, To = now + 60_000, IncludeDeleted = true });
    Check("显式 include_deleted 才拿得到墓碑", list3.Blocks.Any(b => b.Id == "b1-" + tag && b.Deleted));

    var gl3 = await goals.ListGoalsAsync(new Empty());
    var saved3 = gl3.Goals.FirstOrDefault(g => g.Id == g1.Id);
    Check("删除块后目标累计投入立刻变 45（证明是查询不是存值）", saved3?.TotalMinutes == 45, $"total={saved3?.TotalMinutes}");

    // ---------- 版本冲突 ----------
    var stale = new Block
    {
        Id = "b2-" + tag,
        PieceCode = "code",
        OccurredAt = now - 7200_000,
        DurationMin = 45,
        CreatedAt = now,
        UpdatedAt = now - 60_000,
    };
    var conflict = await blocks.UploadBlocksAsync(new UploadBlocksRequest { Blocks = { stale } });
    Check("旧版本上传被拒（版本冲突）", conflict.AcceptedCount == 0, $"acc={conflict.AcceptedCount}");

    // ---------- 当前目标唯一 + 活跃上限 ----------
    var g2 = new Goal { Id = "g2-" + tag, Title = "学完 Rust", Status = 0, StartedAt = now, CreatedAt = now, UpdatedAt = now, IsCurrent = true };
    await goals.UploadGoalsAsync(new UploadGoalsRequest { Goals = { g2 } });
    var gl4 = await goals.ListGoalsAsync(new Empty());
    Check("当前目标全局唯一", gl4.Goals.Count(g => g.IsCurrent && !g.Deleted) == 1,
        $"current={gl4.Goals.Count(g => g.IsCurrent)}");

    var g3 = new Goal { Id = "g3-" + tag, Title = "读写能力", Status = 0, StartedAt = now, CreatedAt = now, UpdatedAt = now };
    var g4 = new Goal { Id = "g4-" + tag, Title = "第四个", Status = 0, StartedAt = now, CreatedAt = now, UpdatedAt = now };
    await goals.UploadGoalsAsync(new UploadGoalsRequest { Goals = { g3 } });
    var over = await goals.UploadGoalsAsync(new UploadGoalsRequest { Goals = { g4 } });
    Check("第 4 个活跃目标被拦下", over.AcceptedCount == 0 && over.ErrorMsg.Contains("暂停"),
        $"acc={over.AcceptedCount} err={over.ErrorMsg}");

    Console.WriteLine();
    Console.WriteLine($"==== R2 断言：通过 {pass}，失败 {fail} ====");
    Environment.ExitCode = fail == 0 ? 0 : 1;
}

static async Task RunR4Checks(GrpcChannel channel)
{
    var plans = new PlanService.PlanServiceClient(channel);
    string day = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    int pass = 0, fail = 0;
    void Check(string name, bool ok)
    {
        if (ok) { pass++; Console.WriteLine($"  [OK]   {name}"); }
        else { fail++; Console.WriteLine($"  [FAIL] {name}"); }
    }

    // 1) 一天最多 3 件：第 4 件被拒
    var req = new UploadPlansRequest();
    foreach (var code in new[] { "code", "doc", "sport", "scroll" })
        req.Plans.Add(new Plan { Id = "probe-" + code, Day = day, PieceCode = code, BudgetMin = 60, CreatedAt = now });

    var resp = await plans.UploadPlansAsync(req);
    Check("4 条里只接受 3 条", resp.AcceptedCount == 3);
    Check("第 4 条进了 failed_ids", resp.FailedIds.Count == 1);
    Check("错误话说清上限", (resp.ErrorMsg ?? "").Contains("最多"));

    // 2) 查询返回 3 条
    var list = await plans.ListPlansAsync(new ListPlansRequest { Day = day });
    Check("当日 3 条", list.Plans.Count == 3);

    // 3) 删除一条后可以再加
    var del = await plans.UploadPlansAsync(new UploadPlansRequest
    {
        Plans = { new Plan { Id = "probe-code", Day = day, PieceCode = "code", Deleted = true, UpdatedAt = now + 10 } }
    });
    Check("软删被接受", del.AcceptedCount == 1);

    var again = await plans.UploadPlansAsync(new UploadPlansRequest
    {
        Plans = { new Plan { Id = "probe-meal", Day = day, PieceCode = "meal", BudgetMin = 30, CreatedAt = now } }
    });
    Check("删一条后能补一条", again.AcceptedCount == 1);

    var list2 = await plans.ListPlansAsync(new ListPlansRequest { Day = day });
    Check("仍然是 3 条（墓碑不算）", list2.Plans.Count == 3);
    Check("墓碑默认不返回", list2.Plans.All(x => x.Id != "probe-code"));

    // 4) 非法日期被拒
    var bad = await plans.UploadPlansAsync(new UploadPlansRequest
    {
        Plans = { new Plan { Id = "probe-bad", Day = "2026/10/18", PieceCode = "code" } }
    });
    Check("非法日期被拒", bad.AcceptedCount == 0 && (bad.ErrorMsg ?? "").Contains("yyyy-MM-dd"));

    // 5) 区间查询（今天 ~ 明天）也能拿到
    long from = new DateTimeOffset(DateTime.Today, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
    var range = await plans.ListPlansAsync(new ListPlansRequest { From = from, To = from + 86_400_000 });
    Check("按区间查得到今天的计划", range.Plans.Count == 3);

    Console.WriteLine($"R4: {pass} 通过 / {fail} 失败");
}

static async Task RunR5Checks(GrpcChannel channel)
{
    var svc = new PatternService.PatternServiceClient(channel);
    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    int pass = 0, fail = 0;
    void Check(string name, bool ok)
    {
        if (ok) { pass++; Console.WriteLine($"  [OK]   {name}"); }
        else { fail++; Console.WriteLine($"  [FAIL] {name}"); }
    }

    // 1) 名称 / 新反应必填
    var empty = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns = { new Pattern { Id = "p-empty" } }
    });
    Check("空名称被拒", empty.AcceptedCount == 0 && (empty.ErrorMsg ?? "").Contains("名称"));

    var noNew = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns = { new Pattern { Id = "p-nonew", Title = "被质疑时不立刻反驳" } }
    });
    Check("缺新反应被拒", noNew.AcceptedCount == 0 && (noNew.ErrorMsg ?? "").Contains("新反应"));

    // 2) 正常建 3 个，第 4 个活跃被拒
    var ok1 = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns =
        {
            new Pattern { Id = "p1", Title = "被质疑时不立刻反驳", NewResponse = "先问「你指的是哪部分」", CreatedAt = now },
            new Pattern { Id = "p2", Title = "22 点后不刷手机", NewResponse = "把手机放客厅", CreatedAt = now },
            new Pattern { Id = "p3", Title = "遇不确定先写三行", NewResponse = "写事实、写猜测、写下一步", CreatedAt = now },
        }
    });
    Check("3 个活跃都建成", ok1.AcceptedCount == 3);

    var fourth = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns = { new Pattern { Id = "p4", Title = "第四个", NewResponse = "随便", CreatedAt = now } }
    });
    Check("第 4 个活跃被拒", fourth.AcceptedCount == 0 && (fourth.ErrorMsg ?? "").Contains("暂停"));

    // 3) 暂停一个后能建新的
    var pause = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns = { new Pattern { Id = "p2", Title = "22 点后不刷手机", NewResponse = "把手机放客厅", Status = 1, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10 } }
    });
    Check("暂停成功", pause.AcceptedCount == 1);

    var p4 = await svc.UploadPatternsAsync(new UploadPatternsRequest
    {
        Patterns = { new Pattern { Id = "p4", Title = "第四个", NewResponse = "随便", CreatedAt = now } }
    });
    Check("腾名额后能建", p4.AcceptedCount == 1);

    var list = await svc.ListPatternsAsync(new Empty());
    Check("列表 4 条", list.Patterns.Count == 4);
    Check("列表带新反应原文", list.Patterns.Any(x => x.NewResponse.Contains("你指的是哪部分")));

    // 4) 练习：result 只能是 0/1，缺 pattern_id 被拒
    var badResult = await svc.UploadPracticesAsync(new UploadPracticesRequest
    {
        Practices = { new Practice { Id = "pr-bad", PatternId = "p1", Result = 5, OccurredAt = now } }
    });
    Check("result=5 被拒", badResult.AcceptedCount == 0);

    var noPattern = await svc.UploadPracticesAsync(new UploadPracticesRequest
    {
        Practices = { new Practice { Id = "pr-nopat", Result = 1, OccurredAt = now } }
    });
    Check("缺模式 id 被拒", noPattern.AcceptedCount == 0);

    var good = await svc.UploadPracticesAsync(new UploadPracticesRequest
    {
        Practices =
        {
            new Practice { Id = "pr1", PatternId = "p1", Result = 1, OccurredAt = now - 3600_000, CreatedAt = now },
            new Practice { Id = "pr2", PatternId = "p1", Result = 0, OccurredAt = now - 7200_000, CreatedAt = now },
        }
    });
    Check("两次练习都收下", good.AcceptedCount == 2);

    var pl = await svc.ListPracticesAsync(new ListPracticesRequest { PatternId = "p1", From = now - 86_400_000, To = now });
    Check("按模式查到 2 条", pl.Practices.Count == 2);

    // 5) 软删练习后默认查不到
    var del = await svc.UploadPracticesAsync(new UploadPracticesRequest
    {
        Practices = { new Practice { Id = "pr2", PatternId = "p1", Result = 0, OccurredAt = now - 7200_000, Deleted = true, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20 } }
    });
    Check("软删练习成功", del.AcceptedCount == 1);

    var pl2 = await svc.ListPracticesAsync(new ListPracticesRequest { PatternId = "p1", From = now - 86_400_000, To = now });
    Check("墓碑默认不返回", pl2.Practices.Count == 1);

    Console.WriteLine($"R5: {pass} 通过 / {fail} 失败");
}
