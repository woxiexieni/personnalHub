using Grpc.Core;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 问题卡接口：上传（手机端离线写、联网补传）+ 查询（手机恢复 / 管理端问题卡页）。
    ///
    /// 校验全在 <see cref="ProblemStore.Save"/>，这里只负责"逐条返回失败 id"——
    /// 一条判据为空的坏卡不该把整批待同步的卡废掉。
    ///
    /// 注意：客户端**不会**因为 failed 就丢弃本地记录（Synced 保持 false，下次重试），
    /// 所以这里的失败必须是"重试也不会成功"的那种（标题/判据为空），
    /// 不能把临时冲突误报成失败。
    /// </summary>
    public sealed class ProblemServiceImpl : ProblemService.ProblemServiceBase
    {
        private readonly ProblemStore _problems;

        public ProblemServiceImpl(ProblemStore problems) => _problems = problems;

        public override Task<UploadProblemsResponse> UploadProblems(
            UploadProblemsRequest request, ServerCallContext context)
        {
            var resp = new UploadProblemsResponse();

            foreach (var p in request.Problems)
            {
                var r = _problems.Save(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<ProblemList> ListProblems(
            ListProblemsRequest request, ServerCallContext context)
        {
            var list = new ProblemList();

            list.Problems.AddRange(_problems.List(
                request.Status,
                request.From,
                request.To,
                request.IncludeDeleted));

            return Task.FromResult(list);
        }
    }
}
