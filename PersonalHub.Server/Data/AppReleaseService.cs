using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace personalHub.Server.Data
{
    /// <summary>一个已经投递到服务端的安卓安装包。</summary>
    public sealed class AppRelease
    {
        /// <summary>磁盘上的文件名（也是下载路由里的名字）。</summary>
        public string FileName { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        /// <summary>安卓 versionCode。只有脚本投递的包才有准确值，手拷的包这里是从文件名推不出来的，填 0。</summary>
        public int Code { get; init; }

        public long SizeBytes { get; init; }

        /// <summary>脚本算的 sha256。手拷进目录的包没有，页面上会显示"未校验"。</summary>
        public string? Sha256 { get; init; }

        public DateTimeOffset? BuiltAt { get; init; }

        /// <summary>
        /// latest.json 里记的大小和磁盘实际大小不一致。
        /// 典型原因是复制到一半被打断，或者有人手动换过文件 —— 这种包装上去必坏，
        /// 必须在页面上说出来，不能让人一边下载一边以为没事。
        /// </summary>
        public bool SizeMismatch { get; init; }

        /// <summary>这个包有没有脚本写的校验信息。false = 手拷的，装之前没有任何保证。</summary>
        public bool Verified => Sha256 is { Length: > 0 };

        public string SizeText => FormatSize(SizeBytes);

        public string BuiltAtText => BuiltAt is null
            ? "未知"
            : BuiltAt.Value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        /// <summary>页面上给人肉眼比对用的短指纹。</summary>
        public string ShaShort => Sha256 is { Length: >= 16 } s ? s.Substring(0, 16) : (Sha256 ?? string.Empty);

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }
    }

    /// <summary>
    /// 手机端安装包的投递目录（&lt;仓库根&gt;\updates）。
    ///
    /// 存在的理由：以前换一个版本要「开电脑 → 插 USB → adb install → 报签名冲突」，
    /// 而手机本来就连着这台电脑的局域网。把包放在服务端，手机浏览器打开一个地址就能下载安装，
    /// 这一步不需要写任何客户端代码，先把"不插 USB"这件事落地。
    ///
    /// 三条刻意的设计：
    ///  1. **每次请求都重扫目录，不缓存**。发布脚本投递完立刻就能下载，不用重启服务端
    ///     （跟 ai.json 的热加载同一个道理：重启服务端在这个项目里等于"手机要重连"）。
    ///  2. **latest.json 只是"补充信息"，不是存在的前提**。手拷一个 apk 进来也能下载，
    ///     只是页面上会标"未校验"——能下载 > 卡着不能用。
    ///  3. **这个端口在局域网里谁都能访问**。所以文件名走白名单正则 + 目录前缀双重校验，
    ///     绝不接受任何带路径成分的输入（见 <see cref="Resolve"/>）。
    /// </summary>
    public sealed class AppReleaseService
    {
        /// <summary>目录名。和 backups/ 平级，都在仓库根。</summary>
        public const string FolderName = "updates";

        /// <summary>
        /// 允许下载的文件名。由 scripts\publish-apk-to-server.ps1 生成，形如
        /// personalhub-1.0.2-arm64.apk。三个数字是版本，后面是 ABI 之类的标记。
        ///
        /// 用白名单而不是"排除 .."：黑名单永远漏，白名单漏不掉 ——
        /// 名字对不上正则就直接 404，path traversal 连门都进不来。
        /// </summary>
        private static readonly Regex PackageName = new(
            @"^personalhub-\d{1,4}\.\d{1,2}\.\d{1,2}-[a-z0-9]{2,12}\.apk$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonRead = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public AppReleaseService(Db db)
        {
            // 默认跟 personalhub.db3、backups/ 放在一起，找起来不用猜。
            // 允许用环境变量指到别处（比如想放到一块独立盘上），但不做任何云端上传。
            string? custom = Environment.GetEnvironmentVariable("PERSONALHUB_UPDATE_DIR");
            string dir = string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Path.GetDirectoryName(db.Path) ?? ".", FolderName)
                : custom;

            // 必须 GetFullPath：db 路径里可能带 ".."（从 bin\Debug 起算），
            // 拼出来的字符串会变成 bin\Debug\..\..\updates 这种难看又难比对的形式，
            // 而前缀校验（Resolve）依赖两边都是规范化的绝对路径。
            Directory = Path.GetFullPath(dir);
        }

        /// <summary>投递目录的绝对路径。</summary>
        public string Directory { get; }

        /// <summary>目录里的全部安装包（新 → 旧）。没有目录、没有包都返回空集合，不抛异常。</summary>
        public IReadOnlyList<AppRelease> All()
        {
            if (!System.IO.Directory.Exists(Directory)) return Array.Empty<AppRelease>();

            Manifest? manifest = ReadManifest();
            var list = new List<AppRelease>();

            foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.apk"))
            {
                string name = Path.GetFileName(path);
                if (!PackageName.IsMatch(name)) continue;   // 陌生文件名一律不当成我们的包

                long size;
                try { size = new FileInfo(path).Length; }
                catch { continue; }

                // 0 字节 = 复制中断留下的残骸，列表里不显示（不然点下载得到个坏包）
                if (size <= 0) continue;

                bool fromManifest = manifest?.File is { Length: > 0 } f
                                    && string.Equals(f, name, StringComparison.OrdinalIgnoreCase);

                list.Add(new AppRelease
                {
                    FileName = name,
                    Version = VersionFromName(name),
                    Code = fromManifest && manifest!.Code > 0 ? manifest!.Code : 0,
                    SizeBytes = size,
                    Sha256 = fromManifest ? manifest!.Sha256 : null,
                    BuiltAt = fromManifest ? manifest!.BuiltAt : SafeWriteTime(path),
                    SizeMismatch = fromManifest && manifest!.Size > 0 && manifest!.Size != size,
                });
            }

            // 按版本号排，不按文件时间：重新投递一个旧版本时，时间会骗人。
            return list
                .OrderByDescending(r => ParseVersion(r.Version))
                .ThenByDescending(r => r.BuiltAt ?? DateTimeOffset.MinValue)
                .ToList();
        }

        /// <summary>最新的一版。没有就返回 null（页面上会说明"还没投递过"）。</summary>
        public AppRelease? Latest() => All().FirstOrDefault();

        /// <summary>
        /// 把路由里的文件名解析成磁盘路径。任何不正常都返回 null（调用方一律 404）。
        /// 三步校验缺一不可：名字合规 → 不含目录成分 → 规范化后仍在投递目录内。
        /// </summary>
        public string? Resolve(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!PackageName.IsMatch(name)) return null;

            // 正则已经排除了 "/" 和 "\"，这一行是给"以后正则被放宽"留的第二道闸。
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return null;

            string full;
            try { full = Path.GetFullPath(Path.Combine(Directory, name)); }
            catch { return null; }

            string root = Directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? Directory
                : Directory + Path.DirectorySeparatorChar;

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            return File.Exists(full) ? full : null;
        }

        // ============================================================
        // latest.json
        // ============================================================

        private sealed class Manifest
        {
            public string? Version { get; set; }
            public int Code { get; set; }
            public string? File { get; set; }
            public long Size { get; set; }
            public string? Sha256 { get; set; }
            public DateTimeOffset? BuiltAt { get; set; }
        }

        /// <summary>读 latest.json。文件不存在 / 内容坏了都返回 null —— 退回"只靠目录扫描"。</summary>
        private Manifest? ReadManifest()
        {
            try
            {
                string path = Path.Combine(Directory, "latest.json");
                if (!File.Exists(path)) return null;

                // 脚本是先写 .tmp 再 Move，正常不会读到半个文件。这里仍然按"读不到就算了"处理：
                // 一个坏掉的描述文件不该让整个下载页打不开。
                var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonRead);
                if (m is null) return null;

                // File 字段直接来自磁盘上的 json（可能被人手改），必须过一遍白名单，
                // 否则 All() 里会用它去比对，等于把一个任意字符串当成了可信输入。
                if (m.File is { Length: > 0 } && !PackageName.IsMatch(m.File)) m.File = null;
                if (m.Size < 0) m.Size = 0;

                return m;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // 小工具
        // ============================================================

        private static string VersionFromName(string fileName)
        {
            // personalhub-1.0.2-arm64.apk -> 1.0.2
            string body = fileName.Substring("personalhub-".Length);
            int dash = body.IndexOf('-');
            return dash > 0 ? body.Substring(0, dash) : body;
        }

        private static Version ParseVersion(string text)
        {
            return Version.TryParse(text, out var v) ? v : new Version(0, 0);
        }

        private static DateTimeOffset? SafeWriteTime(string path)
        {
            try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
            catch { return null; }
        }

        // ============================================================
        // 下载页
        // ============================================================

        /// <summary>
        /// 给手机浏览器看的下载页。移动优先、深浅色自适应、零外部依赖（断网/内网也必须能开）。
        /// </summary>
        public string RenderPage()
        {
            var latest = Latest();
            var all = All();

            var sb = new StringBuilder(8192);
            sb.Append("""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#f6f5f1">
<title>PersonalHub 安装包</title>
<style>
:root{--bg:#f6f5f1;--panel:#fff;--line:#e3e0d8;--text:#23231f;--muted:#78766e;--accent:#185fa5;--chip:#f1efe8;--warn-bg:#fff4e5;--warn-line:#f0d3aa;--warn-text:#8a5200}
@media (prefers-color-scheme: dark){:root{--bg:#161614;--panel:#1f1f1c;--line:#33332e;--text:#eceae4;--muted:#9a988f;--accent:#6fa8dc;--chip:#26261f;--warn-bg:#2e2314;--warn-line:#5a431f;--warn-text:#e0b579}}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Microsoft YaHei",sans-serif;font-size:15px;line-height:1.6;-webkit-text-size-adjust:100%}
.wrap{max-width:520px;margin:0 auto;padding:28px 18px calc(28px + env(safe-area-inset-bottom))}
h1{font-size:20px;font-weight:700;margin:0}
.sub{color:var(--muted);font-size:13px;margin:2px 0 20px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px;margin-bottom:14px}
.ver{font-size:32px;font-weight:700;letter-spacing:-.5px;font-variant-numeric:tabular-nums}
.meta{color:var(--muted);font-size:13px;font-variant-numeric:tabular-nums}
.btn{display:block;margin-top:16px;background:var(--accent);color:#fff;text-decoration:none;text-align:center;padding:15px;border-radius:11px;font-size:16px;font-weight:600}
.btn:active{opacity:.85}
.hint{color:var(--muted);font-size:13px;margin:12px 0 0}
.warn{background:var(--warn-bg);border:1px solid var(--warn-line);color:var(--warn-text);border-radius:10px;padding:10px 12px;font-size:13px;margin-top:14px}
.empty{color:var(--muted);font-size:14px}
code{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12px;background:var(--chip);padding:2px 5px;border-radius:4px;word-break:break-all}
details{margin-top:14px}
summary{color:var(--muted);font-size:12px;cursor:pointer}
ol{padding-left:20px;margin:8px 0 0;color:var(--muted);font-size:13px}
ol li{margin:3px 0}
.hist{margin-top:6px}
.hist a{display:flex;justify-content:space-between;gap:10px;padding:11px 0;border-top:1px solid var(--line);color:var(--text);text-decoration:none;font-size:14px}
.hist a:first-child{border-top:none}
.hist .s{color:var(--muted);font-variant-numeric:tabular-nums;font-size:13px}
.foot{color:var(--muted);font-size:12px;text-align:center;margin-top:22px}
</style>
</head>
<body>
<div class="wrap">
<h1>PersonalHub</h1>
<p class="sub">手机端安装包 · 直接从这台电脑下载</p>
""");

            if (latest is null)
            {
                sb.Append("""
<div class="card">
  <p class="empty">这里还没有安装包。</p>
  <p class="hint">在电脑上运行一次 <code>scripts\client-android-release.bat</code>，
打包成功后会自动投递到这个目录，刷新本页就能下载。</p>
</div>
""");
            }
            else
            {
                sb.Append("<div class=\"card\">\n");
                sb.Append("<div class=\"ver\">").Append(Escape(latest.Version)).Append("</div>\n");
                sb.Append("<div class=\"meta\">").Append(latest.SizeText).Append(" · ").Append(Escape(latest.BuiltAtText)).Append("</div>\n");
                if (latest.Code > 0)
                    sb.Append("<div class=\"meta\">versionCode ").Append(latest.Code).Append("</div>\n");

                sb.Append("<a class=\"btn\" href=\"/app/download/").Append(Escape(latest.FileName)).Append("\">下载并安装</a>\n");

                sb.Append("<p class=\"hint\">下载完点一下文件，系统会问「是否允许安装」→ 选允许。<br>"
                        + "这是覆盖安装，App 里的数据不会被清掉。</p>\n");

                if (latest.SizeMismatch)
                {
                    sb.Append("<div class=\"warn\">这个包的大小和记录的对不上（记录 ")
                      .Append(AppRelease.FormatSize(latest.SizeBytes == 0 ? 0 : latest.SizeBytes))
                      .Append("，可能是复制中断）。建议在电脑上重新投递一次，不要装这个。</div>\n");
                }

                if (!latest.Verified)
                {
                    sb.Append("<div class=\"warn\">这个包是手工拷进来的，没有校验信息。"
                            + "想确认没下坏，用 <code>scripts\\client-android-release.bat</code> 投递一次。</div>\n");
                }

                if (latest.Verified)
                {
                    sb.Append("<details><summary>校验信息</summary><code>sha256 ")
                      .Append(Escape(latest.Sha256!)).Append("</code></details>\n");
                }

                sb.Append("</div>\n");
            }

            if (all.Count > 1)
            {
                sb.Append("<div class=\"card\"><div class=\"meta\" style=\"margin-bottom:4px\">之前的版本</div><div class=\"hist\">\n");
                foreach (var r in all.Skip(1))
                {
                    sb.Append("<a href=\"/app/download/").Append(Escape(r.FileName)).Append("\">")
                      .Append("<span>").Append(Escape(r.Version)).Append("</span>")
                      .Append("<span class=\"s\">").Append(r.SizeText).Append("</span></a>\n");
                }
                sb.Append("</div></div>\n");
            }

            sb.Append("""
<div class="card">
  <div class="meta" style="margin-bottom:4px">第一次用，看这里</div>
  <ol>
    <li>手机要连和这台电脑同一个 Wi-Fi</li>
    <li>点上面的「下载并安装」，等进度条走完</li>
    <li>在通知栏或「下载」里点那个文件，按提示装完</li>
  </ol>
</div>
<p class="foot">手机装不上、或者 App 已经打不开时，才用 USB：<code>scripts\install-apk.bat</code></p>
</div>
</body>
</html>
""");

            return sb.ToString();
        }

        /// <summary>
        /// HTML 转义。版本号/文件名本来都是我们自己写的，但 latest.json 在磁盘上、
        /// 谁都能改，页面直接回显就成了一个免费 XSS —— 转义成本几乎为零，没有理由省。
        /// </summary>
        private static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&#39;", StringComparison.Ordinal);
        }
    }
}
