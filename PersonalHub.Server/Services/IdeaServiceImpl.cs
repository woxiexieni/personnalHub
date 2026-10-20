using Grpc.Core;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 想法接口：上传（手机端离线写、联网补传）+ 查询（手机列表 / 管理端想法库）。
    ///
    /// 校验全在 <see cref="IdeaStore.Save"/>，这里只负责"逐条返回失败 id"，
    /// 避免一条坏数据把整个批次废掉（手机上待同步的记录会一直被重试）。
    /// </summary>
    public sealed class IdeaServiceImpl : IdeaService.IdeaServiceBase
    {
        private readonly IdeaStore _ideas;

        public IdeaServiceImpl(IdeaStore ideas) => _ideas = ideas;

        public override Task<UploadIdeasResponse> UploadIdeas(
            UploadIdeasRequest request, ServerCallContext context)
        {
            var resp = new UploadIdeasResponse();

            foreach (var p in request.Ideas)
            {
                var r = _ideas.Save(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<IdeaList> ListIdeas(ListIdeasRequest request, ServerCallContext context)
        {
            var list = new IdeaList();

            list.Ideas.AddRange(_ideas.List(
                request.Kind,
                request.From,
                request.To,
                request.IncludeDeleted));

            return Task.FromResult(list);
        }
    }
}
