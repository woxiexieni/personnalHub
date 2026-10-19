using Grpc.Core;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 行为模式 / 练习接口。
    ///
    /// 与其它 Service 完全同构：逐条保存、失败 id 单独返回，
    /// 一条坏数据不至于废掉整批（否则手机端会一直重试那一条）。
    /// </summary>
    public sealed class PatternServiceImpl : PatternService.PatternServiceBase
    {
        private readonly PatternStore _store;

        public PatternServiceImpl(PatternStore store) => _store = store;

        public override Task<UploadPatternsResponse> UploadPatterns(
            UploadPatternsRequest request, ServerCallContext context)
        {
            var resp = new UploadPatternsResponse();

            foreach (var p in request.Patterns)
            {
                var r = _store.SavePattern(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<PatternList> ListPatterns(Empty request, ServerCallContext context)
        {
            var list = new PatternList();
            list.Patterns.AddRange(_store.ListPatterns());
            return Task.FromResult(list);
        }

        public override Task<UploadPracticesResponse> UploadPractices(
            UploadPracticesRequest request, ServerCallContext context)
        {
            var resp = new UploadPracticesResponse();

            foreach (var p in request.Practices)
            {
                var r = _store.SavePractice(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<PracticeList> ListPractices(
            ListPracticesRequest request, ServerCallContext context)
        {
            long from = request.From != 0 ? request.From : 0;
            long to = request.To != 0 ? request.To : long.MaxValue;

            var list = new PracticeList();
            list.Practices.AddRange(_store.ListPractices(
                from, to,
                string.IsNullOrWhiteSpace(request.PatternId) ? null : request.PatternId,
                request.IncludeDeleted));

            return Task.FromResult(list);
        }
    }
}
