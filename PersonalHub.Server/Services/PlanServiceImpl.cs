using Grpc.Core;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 每日计划接口：上传（手机端离线写、联网补传）+ 查询（手机今日 / 管理端当日对比）。
    ///
    /// 校验全部落在 <see cref="PlanStore.Save"/>，这里只负责"逐条返回失败 id"，
    /// 避免一条坏数据把整个批次废掉（手机上待同步的记录会一直被重试）。
    /// </summary>
    public sealed class PlanServiceImpl : PlanService.PlanServiceBase
    {
        private readonly PlanStore _plans;

        public PlanServiceImpl(PlanStore plans) => _plans = plans;

        public override Task<UploadPlansResponse> UploadPlans(
            UploadPlansRequest request, ServerCallContext context)
        {
            var resp = new UploadPlansResponse();

            foreach (var p in request.Plans)
            {
                var r = _plans.Save(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<PlanList> ListPlans(ListPlansRequest request, ServerCallContext context)
        {
            var list = new PlanList();

            if (!string.IsNullOrWhiteSpace(request.Day))
            {
                list.Plans.AddRange(_plans.ListDay(request.Day, request.IncludeDeleted));
                return Task.FromResult(list);
            }

            // from/to 是毫秒；转成 UTC+8 的日期字符串再做区间比较 —— 计划的 day 是自然日
            string fromDay = request.From != 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(request.From)
                    .ToOffset(Analysis.Period.Cst).ToString("yyyy-MM-dd")
                : "0000-01-01";

            string toDay = request.To != 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(request.To - 1)
                    .ToOffset(Analysis.Period.Cst).ToString("yyyy-MM-dd")
                : "9999-12-31";

            list.Plans.AddRange(_plans.ListRange(fromDay, toDay, request.IncludeDeleted));
            return Task.FromResult(list);
        }
    }
}
