using System.Text.Json;

namespace personalHub.Server.Ai
{
    /// <summary>
    /// AI 配置：一个 ai.json，**只在服务端**，客户端永远拿不到 key。
    ///
    /// 为什么不是 appsettings.json：
    ///   1. appsettings.json 是"框架的东西"，改它容易顺手把别的配置一起改坏；
    ///   2. ai.json 可以整份加进 .gitignore，而 appsettings.json 通常是进仓库的 ——
    ///      key 一旦进过 git 历史，就永远在里面了。
    ///
    /// 为什么不是管理端页面来填：
    ///   管理端跑在 5001，局域网里任何设备都能打开。多一个能写 key 的页面，
    ///   就多一个能被人改掉 key 的入口；而填 key 这件事一辈子只做一次。
    ///
    /// 热加载：每次取配置都比一次文件修改时间，变了就重读。
    /// 于是「打开 ai.json 填 key → 保存 → 回手机点一下」就能用，不用重启服务端。
    /// </summary>
    public sealed class AiConfig
    {
        /// <summary>文件里 key 的字段名（模板里也用它）。</summary>
        private const string KeyField = "apiKey";

        private readonly object _sync = new();
        private DateTime _loadedAt = DateTime.MinValue;
        private Snapshot _current;
        public AiConfig()
        {
            Path = ResolvePath();
            _current = Load(Path, createIfMissing: true);
        }

        /// <summary>配置文件绝对路径（没配 key 时会把这句话显示给用户，他照着去打开）。</summary>
        public string Path { get; }

        /// <summary>当前生效的一份配置（文件变了会自动重读）。</summary>
        public Snapshot Current
        {
            get
            {
                lock (_sync)
                {
                    try
                    {
                        var mtime = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
                        if (mtime != _loadedAt) _current = Load(Path, createIfMissing: true);
                    }
                    catch
                    {
                        // 读不到就继续用上一份。配置文件坏了不该让服务端跟着坏。
                    }
                    return _current;
                }
            }
        }

        /// <summary>一份不可变快照，避免调用过程中配置被热加载换掉。</summary>
        public sealed record Snapshot(
            bool Enabled, string Endpoint, string Model, string ApiKey, int TimeoutSeconds, string Path)
        {
            public bool Configured =>
                Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Endpoint);

            /// <summary>给界面看的一句话（不含 key 本身，只有一个"配了/没配"）。</summary>
            public string Hint => Configured
                ? $"已配置 · 模型 {Model}"
                : $"还没填 key：打开 {Path}，把 \"{KeyField}\" 填成你的 DeepSeek key（sk- 开头），保存即可，不用重启。";
        }

        // ============================================================
        // 读 / 写
        // ============================================================

        private Snapshot Load(string path, bool createIfMissing)
        {
            try
            {
                if (!File.Exists(path) && createIfMissing) WriteTemplate(path);
                if (!File.Exists(path))
                {
                    _loadedAt = DateTime.MinValue;
                    return Defaults(path);
                }

                _loadedAt = File.GetLastWriteTimeUtc(path);

                string json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<Template>(json, JsonOpts) ?? new Template();

                // 环境变量优先于文件：临时换 key / CI 里跑，不必去改文件。
                string? envKey = Environment.GetEnvironmentVariable("PERSONALHUB_LLM_KEY");
                string key = !string.IsNullOrWhiteSpace(envKey) ? envKey.Trim() : (dto.ApiKey ?? string.Empty).Trim();

                return new Snapshot(
                    Enabled: dto.Enabled ?? true,
                    Endpoint: string.IsNullOrWhiteSpace(dto.Endpoint) ? DefaultEndpoint : dto.Endpoint!.Trim(),
                    Model: string.IsNullOrWhiteSpace(dto.Model) ? DefaultModel : dto.Model!.Trim(),
                    ApiKey: key,
                    TimeoutSeconds: dto.TimeoutSeconds is > 1 and <= 120 ? dto.TimeoutSeconds!.Value : 25,
                    Path: path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] 读取配置失败（{path}）：{ex.Message}");
                _loadedAt = DateTime.MinValue;
                return Defaults(path);
            }
        }

        private static Snapshot Defaults(string path) =>
            new(true, DefaultEndpoint, DefaultModel, string.Empty, 25, path);

        /// <summary>第一次启动时把模板写出来 —— 让用户能"照着打开填一行"，而不是来问路径。</summary>
        private static void WriteTemplate(string path)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(path)) return;

                File.WriteAllText(path, """
                {
                  "_说明": "填 apiKey 后保存即可生效，不用重启服务端。这个文件不要提交到 git（已在 .gitignore 里）。",
                  "_字段": {
                    "enabled": "false 可以整体关掉 AI，客户端会自动走本地规则版",
                    "endpoint": "DeepSeek 的对话补全地址；换别的 OpenAI 兼容服务也改这里",
                    "model": "必须和你在 DeepSeek 控制台看到的模型名一字不差",
                    "apiKey": "sk- 开头；也可以用环境变量 PERSONALHUB_LLM_KEY 覆盖",
                    "timeoutSeconds": "单次调用超时（秒），超了就降级到规则版"
                  },
                  "enabled": true,
                  "endpoint": "https://api.deepseek.com/chat/completions",
                  "model": "deepseek-flash",
                  "apiKey": "",
                  "timeoutSeconds": 25
                }
                """);

                Console.WriteLine($"[AI] 已生成配置模板：{path}");
                Console.WriteLine($"[AI]   → 用记事本打开它，把 \"apiKey\" 填上就能用（不用重启）。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AI] 无法生成配置模板（{path}）：{ex.Message}");
            }
        }

        /// <summary>
        /// 路径优先级：PERSONALHUB_AI 环境变量 → 仓库根目录 ai.json → 程序目录 ai.json。
        /// 与 personalhub.db3 完全一致 —— 开发时两个文件并排放，一眼就能看到。
        /// </summary>
        private static string ResolvePath()
        {
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_AI");
            if (!string.IsNullOrWhiteSpace(custom)) return custom;

            string baseDir = AppContext.BaseDirectory;
            string root = FindSolutionRoot(baseDir) ?? baseDir;
            return System.IO.Path.Combine(root, "ai.json");
        }

        private static string? FindSolutionRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetFiles("*.sln").Length > 0) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";
        private const string DefaultModel = "deepseek-flash";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>反序列化用的中间类型（字段全是可空的，缺字段不该当成错误）。</summary>
        private sealed class Template
        {
            public bool? Enabled { get; set; }
            public string? Endpoint { get; set; }
            public string? Model { get; set; }
            public string? ApiKey { get; set; }
            public int? TimeoutSeconds { get; set; }
        }
    }
}
