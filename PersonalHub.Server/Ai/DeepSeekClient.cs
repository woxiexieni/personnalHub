using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace personalHub.Server.Ai
{
    /// <summary>一次模型调用的结果。失败不抛异常 —— 调用方只关心"成不成、为什么"。</summary>
    public sealed record LlmReply(bool Ok, string Content, string Error, long ElapsedMs);

    /// <summary>
    /// DeepSeek 的 HTTP 客户端（OpenAI 兼容的 /chat/completions）。
    ///
    /// 为什么自己做而不用 SDK：这个项目对它的要求只有"发两段文字、要一段 JSON"，
    /// 引一个 SDK 只会多一组版本冲突（Grpc.Tools / sqlite-net 已经够多了）。
    ///
    /// 失败一律折成 <see cref="LlmReply.Ok"/>=false，**永不抛到 gRPC 层**：
    /// 用户点「帮我想」时，AI 挂了应该表现得像"这次没想出来"，而不是一个红色报错。
    /// </summary>
    public sealed class DeepSeekClient
    {
        // HttpClient 要复用（每次 new 会耗尽 socket）。超时用 CTS 单独控制，
        // 因为配置文件里的 timeoutSeconds 是热加载的，不能钉在建客户端那一刻。
        private static readonly HttpClient Http = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        /// <summary>要模型输出 JSON 时，把 user 里也塞一句"只输出 JSON"（DeepSeek 的 json 模式要求提到 json）。</summary>
        public async Task<LlmReply> ChatJsonAsync(
            AiConfig.Snapshot cfg, string system, string user, CancellationToken ct)
        {
            if (!cfg.Configured)
            {
                return new LlmReply(false, string.Empty, "没配置 AI（ai.json 里 apiKey 为空）", 0);
            }

            var sw = Stopwatch.StartNew();

            // 先带 response_format 试一次；某些兼容实现不认这个字段会返回 400，
            // 那就去掉它再试一次 —— 这比在提示词里反复强调"只要 JSON"更稳。
            var first = await SendAsync(cfg, system, user, jsonMode: true, ct).ConfigureAwait(false);
            if (first.Ok) return first with { ElapsedMs = sw.ElapsedMilliseconds };

            if (first.Error.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            {
                var second = await SendAsync(cfg, system, user, jsonMode: false, ct).ConfigureAwait(false);
                return second with { ElapsedMs = sw.ElapsedMilliseconds };
            }

            return first with { ElapsedMs = sw.ElapsedMilliseconds };
        }

        private static async Task<LlmReply> SendAsync(
            AiConfig.Snapshot cfg, string system, string user, bool jsonMode, CancellationToken outer)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

            object payload = jsonMode
                ? new
                {
                    model = cfg.Model,
                    messages = Msgs(system, user),
                    temperature = 0.6,
                    // 3000 而不是 900：deepseek-flash 这类**推理型**模型会先写思维链
                    // （实测一句"拆成 JSON"就烧掉 535 个 reasoning token），
                    // 900 会被思维链吃光，content 直接是空的。见 §31。
                    max_tokens = 3000,
                    stream = false,
                    response_format = new { type = "json_object" },
                }
                : new
                {
                    model = cfg.Model,
                    messages = Msgs(system, user),
                    temperature = 0.6,
                    max_tokens = 1500,
                    stream = false,
                };

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    return new LlmReply(false, string.Empty,
                        $"HTTP {(int)resp.StatusCode}：{Trim(body, 300)}", 0);
                }

                string? content = ExtractContent(body);
                if (string.IsNullOrWhiteSpace(content))
                {
                    // 区分两种"没 content"：被截断（max_tokens 用完）和结构不对。
                    // 这一条是实测踩出来的：推理型模型会先吐 reasoning_content，
                    // 预算不够时 content 就是空的，而 finish_reason 会明确写着 length。
                    return new LlmReply(false, string.Empty,
                        IsTruncated(body) ? "输出被截断（max_tokens 用完，思维链占掉了）" : "模型返回里没有 content", 0);
                }

                return new LlmReply(true, content!, string.Empty, 0);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !outer.IsCancellationRequested)
            {
                return new LlmReply(false, string.Empty, $"超时（{cfg.TimeoutSeconds} 秒）", 0);
            }
            catch (Exception ex)
            {
                return new LlmReply(false, string.Empty, "调用失败：" + Trim(ex.Message, 200), 0);
            }
        }

        private static object[] Msgs(string system, string user) => new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user },
        };

        /// <summary>choices[0].message.content。结构对不上就返回 null（交给上层当失败处理）。</summary>
        private static string? ExtractContent(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
                if (choices.GetArrayLength() == 0) return null;

                var msg = choices[0].GetProperty("message");
                if (!msg.TryGetProperty("content", out var c)) return null;
                return c.GetString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>choices[0].finish_reason 是不是 length（= 被 max_tokens 截断）。</summary>
        private static bool IsTruncated(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) return false;
                if (choices.GetArrayLength() == 0) return false;
                return choices[0].TryGetProperty("finish_reason", out var fr)
                    && string.Equals(fr.GetString(), "length", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string Trim(string s, int max)
        {
            s = (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
