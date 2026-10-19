using Grpc.Core;
using Microsoft.Extensions.Logging;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 目标服务。极简层：只有名字、状态、一条下一步、当前标记。
    /// 累计投入与最后活动是 List 时现算的（见 <see cref="BlockStore.GoalTotals"/>）。
    /// </summary>
    public class GoalServiceImpl : GoalService.GoalServiceBase
    {
        private readonly ILogger<GoalServiceImpl> _logger;
        private readonly GoalStore _store;

        public GoalServiceImpl(ILogger<GoalServiceImpl> logger, GoalStore store)
        {
            _logger = logger;
            _store = store;
        }

        public override Task<UploadGoalsResponse> UploadGoals(
            UploadGoalsRequest request, ServerCallContext context)
        {
            var failed = new List<string>();
            int accepted = 0;
            string? firstError = null;

            foreach (var g in request.Goals)
            {
                var result = _store.Save(g);

                if (result.Ok)
                {
                    accepted++;
                    continue;
                }

                failed.Add(string.IsNullOrEmpty(g.Id) ? "(无 id)" : g.Id);
                firstError ??= result.Error;
            }

            var resp = new UploadGoalsResponse { AcceptedCount = accepted };
            resp.FailedIds.AddRange(failed);
            if (firstError is not null) resp.ErrorMsg = firstError;

            _logger.LogInformation(
                "UploadGoals: 收到 {Total} 条，通过 {Ok} 条，失败 {Bad} 条",
                request.Goals.Count, accepted, failed.Count);

            return Task.FromResult(resp);
        }

        public override Task<GoalList> ListGoals(Empty request, ServerCallContext context)
        {
            var list = new GoalList();
            list.Goals.AddRange(_store.List());
            return Task.FromResult(list);
        }
    }
}
