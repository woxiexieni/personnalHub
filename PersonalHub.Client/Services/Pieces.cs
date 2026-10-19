using System.Text.Json;

namespace PersonalHub.Client.Services
{
    /// <summary>一个「事情片」：录入页上的一张卡。</summary>
    public sealed record Piece(string Code, string DefaultName, string Group, string Emoji);

    /// <summary>
    /// 事情片清单（23 个，一级，不做层级）。
    ///
    /// 设计约束（来自需求 §4.1）：
    ///   1. **一级，不做树。** 多一层就多一次决策，而录入只有 1 次点击的预算。
    ///   2. `Code` 生成后**永不修改** —— 统计要靠它对上历史。显示名随便改。
    ///   3. 没有 `project`（做项目）这一片：它不是可观测的活动，
    ///      真正在发生的是写代码 / 写文档 / 调试 / 开会。目标层已经承载了"做某个项目"。
    ///
    /// 收录标准：**你在录入时能立刻判断"刚才那段属于它"**。
    /// 判断不了的片（比如"学习"和"读专业书"）就是对使用者不友好，宁可合并。
    /// </summary>
    public static class Pieces
    {
        /// <summary>组的显示顺序。录入页按这个顺序折叠展示。</summary>
        public static readonly string[] GroupOrder =
        {
            "产出", "学习", "身体", "生活", "人际", "消耗", "零散"
        };

        public static readonly IReadOnlyList<Piece> All = new List<Piece>
        {
            new("code",     "写代码",       "产出", "⌨"),
            new("doc",      "写文档",       "产出", "✎"),
            new("meeting",  "开会",         "产出", "◷"),

            new("learn",    "学技术",       "学习", "◈"),
            new("leetcode", "刷题",         "学习", "◇"),
            new("book",     "读专业书",     "学习", "▤"),
            new("english",  "学英语",       "学习", "A"),

            new("sport",    "运动",         "身体", "▲"),
            new("nap",      "午睡",         "身体", "☾"),
            new("meal",     "吃饭",         "身体", "○"),

            new("commute",  "通勤",         "生活", "→"),
            new("chore",    "家务",         "生活", "■"),
            new("tidy",     "收拾",         "生活", "□"),
            new("errand",   "杂事",         "生活", "·"),

            new("chat",     "聊天",         "人际", "◍"),
            new("family",   "陪家人",       "人际", "♥"),
            new("dinner",   "聚餐",         "人际", "◎"),

            new("video",    "短视频",       "消耗", "▷"),
            new("game",     "游戏",         "消耗", "◆"),
            new("show",     "看剧",         "消耗", "▣"),
            new("scroll",   "无目的刷手机", "消耗", "▤"),

            new("idle",     "发呆",         "零散", "…"),
            new("review",   "写复盘",       "零散", "☆"),
        };

        public static Piece? ByCode(string? code)
            => All.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));

        /// <summary>时长档。180 是刻意加的：写代码经常一次超 2 小时，只到 120 会逼人拆错。</summary>
        public static readonly int[] Durations = { 5, 15, 30, 45, 60, 90, 120, 180 };
    }

    /// <summary>
    /// 片的本地偏好：钉子 / 改名 / 隐藏 / 上次用的时长。
    ///
    /// 为什么放 Preferences 而不是数据库：
    /// 这些是**设备偏好**，不是需要同步的数据。多一台设备就该各有一套顺手顺序。
    ///
    /// 为什么要"钉子"而不只是频次排序（需求 §4.1）：
    /// 纯频次会让位置天天漂 —— 今天多点了几次短视频，"写代码"明天就从第一行挪走，
    /// 肌肉记忆失效，每次都要找，省下的决策又还回去了。
    /// **钉子负责"你的常用"，频次只负责填空。**
    /// </summary>
    public sealed class PieceSettings
    {
        private const string Key = "ph.pieces.v1";
        private const string LastDurationKey = "ph.pieces.lastDuration.";

        private State _state;

        public PieceSettings()
        {
            _state = Load();
        }

        /// <summary>钉住的片（有序，最多 6 个，超出不再接受）。</summary>
        public IReadOnlyList<string> Pinned => _state.Pinned;

        public string DisplayName(string code)
        {
            if (_state.Names.TryGetValue(code, out var custom) && !string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
            return Pieces.ByCode(code)?.DefaultName ?? code;
        }

        public bool IsHidden(string code) => _state.Hidden.Contains(code);

        /// <summary>某个片上次用的时长；没有就用 30 分（最常见的一段）。</summary>
        public int DurationFor(string code)
            => _state.LastDuration.TryGetValue(code, out int d) && d > 0 ? d : 30;

        public void SetDuration(string code, int minutes)
        {
            if (minutes <= 0) return;
            _state.LastDuration[code] = minutes;
            Save();
        }

        /// <summary>钉住 / 取消钉住。返回 false 表示已经钉满 6 个。</summary>
        public bool TogglePin(string code)
        {
            if (_state.Pinned.Contains(code))
            {
                _state.Pinned.Remove(code);
                Save();
                return true;
            }

            if (_state.Pinned.Count >= 6) return false;

            _state.Pinned.Add(code);
            Save();
            return true;
        }

        public void Rename(string code, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _state.Names.Remove(code);
            }
            else
            {
                _state.Names[code] = name.Trim();
            }
            Save();
        }

        public void SetHidden(string code, bool hidden)
        {
            if (hidden) _state.Hidden.Add(code);
            else _state.Hidden.Remove(code);
            Save();
        }

        /// <summary>
        /// 前排 6 个：钉子优先（顺序即钉的顺序），空位按最近 7 天点击频次补。
        /// 频次由调用方传进来（它需要读本地库，不该让偏好设置去碰数据库）。
        /// </summary>
        public List<Piece> FrontRow(IReadOnlyDictionary<string, int> freq7d, int slots = 6)
        {
            var result = new List<Piece>();

            foreach (var code in _state.Pinned)
            {
                var p = Pieces.ByCode(code);
                if (p is not null && !IsHidden(code)) result.Add(p);
                if (result.Count >= slots) return result;
            }

            var order = Pieces.All
                .Select((p, i) => (p.Code, Index: i))
                .ToDictionary(x => x.Code, x => x.Index, StringComparer.Ordinal);

            var rest = Pieces.All
                .Where(p => !IsHidden(p.Code) && !_state.Pinned.Contains(p.Code))
                .Select(p => new { Piece = p, Count = freq7d.TryGetValue(p.Code, out int n) ? n : 0 })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => order.TryGetValue(x.Piece.Code, out int i) ? i : int.MaxValue)
                .ToList();

            foreach (var x in rest)
            {
                if (result.Count >= slots) break;
                result.Add(x.Piece);
            }

            return result;
        }

        // ============================================================
        // 存取
        // ============================================================

        private sealed class State
        {
            public List<string> Pinned { get; set; } = new();
            public HashSet<string> Hidden { get; set; } = new();
            public Dictionary<string, string> Names { get; set; } = new();
            public Dictionary<string, int> LastDuration { get; set; } = new();
        }

        private static State Load()
        {
            try
            {
                var json = Preferences.Default.Get(Key, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return new State();

                return JsonSerializer.Deserialize<State>(json) ?? new State();
            }
            catch
            {
                // 偏好坏了不该让录入页打不开：退回默认（没有任何钉子）
                return new State();
            }
        }

        private void Save()
        {
            try
            {
                Preferences.Default.Set(Key, JsonSerializer.Serialize(_state));
            }
            catch
            {
                // 写偏好失败只影响"下次记不记得住"，不影响这次录入
            }
        }

        /// <summary>兼容旧版本用的键（当前未使用，保留以免将来要迁移时长默认值）。</summary>
        public static string LastDurationKeyFor(string code) => LastDurationKey + code;
    }
}
