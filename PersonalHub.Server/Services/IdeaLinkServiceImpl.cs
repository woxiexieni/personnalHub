using Grpc.Core;
using Personalhub;
using personalHub.Server.Data;

namespace personalHub.Server.Services
{
    /// <summary>
    /// 想法关系接口：上传（建边/取消边后补传）+ 查询（换机恢复 / 管理端关系页）。
    ///
    /// 校验全在 <see cref="IdeaLinkStore.Save"/>，这里只负责"逐条返回失败 id"——
    /// 一条结构不对的坏边不该把整批待同步的边废掉。
    /// </summary>
    public sealed class IdeaLinkServiceImpl : IdeaLinkService.IdeaLinkServiceBase
    {
        private readonly IdeaLinkStore _links;

        public IdeaLinkServiceImpl(IdeaLinkStore links) => _links = links;

        public override Task<UploadIdeaLinksResponse> UploadIdeaLinks(
            UploadIdeaLinksRequest request, ServerCallContext context)
        {
            var resp = new UploadIdeaLinksResponse();

            foreach (var p in request.Links)
            {
                var r = _links.Save(p);
                if (r.Ok) resp.AcceptedCount++;
                else
                {
                    resp.FailedIds.Add(p.Id ?? string.Empty);
                    if (string.IsNullOrEmpty(resp.ErrorMsg)) resp.ErrorMsg = r.Error;
                }
            }

            return Task.FromResult(resp);
        }

        public override Task<IdeaLinkList> ListIdeaLinks(
            ListIdeaLinksRequest request, ServerCallContext context)
        {
            var list = new IdeaLinkList();

            list.Links.AddRange(_links.List(
                request.Kind,
                request.IncludeDeleted));

            return Task.FromResult(list);
        }
    }
}
