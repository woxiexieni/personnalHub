using System.Text.Json;

namespace PersonalHub.Client.Services
{
    /// <summary>一个「事情片」：录入页上的一张卡。</summary>
    public sealed record Piece(string Code, string DefaultName, string Group, string Emoji);

    /// <summary>
    /// 事情片清单（12 个，一级，不做层级）。
    ///
    /// 设计约束（来自需求 §4.1）：
    ///   1. **一级，不做树。** 多一层就多一次决策，而录入只有 1 次点击的预算。
    ///   2. `Code` 生成后**永不修改** —— 统计要靠它对上历史。显示名随便改。
    ///   3. 没有 `project`（做项目）这一片：它不是可观测的活动，
    ///      真正在发生的是干活 / 写代码 / 学习。目标层已经承载了"做某个项目"。
    ///
    /// 收录标准（2026-10 按真实数据重切，见需求 §33）：
    ///   **片只回答"这是哪种时间"，不回答"具体在干嘛"。**
    ///   具体在干嘛交给备注；为了谁交给目标。片再往细裂（"写代码-工作/自己"）
    ///   就会按"业务线 × 活动"爆炸，永远加不完。
    ///
    /// 为什么要收缩到 12 个：旧清单 23 个，实际只被点过 8 个，其中 3 个还被当成"不知道该选谁"的筐
    /// （写文档装了"发版/测试"，写代码装了"上班"，午睡装了夜里睡觉）。
    /// 片越多，扫视成本越高，而信息量反而下降。
    /// </summary>
    public static class Pieces
    {
        /// <summary>组的显示顺序。</summary>
        public static readonly string[] GroupOrder =
        {
            "产出", "身体", "生活", "学习", "人际", "消耗", "零散"
        };

        public static readonly IReadOnlyList<Piece> All = new List<Piece>
        {
            new("code",      "写代码",   "产出", "⌨"),
            new("work",      "干活",     "产出", "✎"),
            new("review",    "写复盘",   "产出", "☆"),

            new("sleep",     "睡觉",     "身体", "☾"),
            new("meal",      "吃饭",     "身体", "○"),
            new("sport",     "运动",     "身体", "▲"),

            new("commute",   "通勤",     "生活", "→"),
            new("chore",     "家务杂事", "生活", "■"),

            new("learn",     "学习",     "学习", "◈"),

            new("people",    "陪人",     "人际", "♥"),

            new("entertain", "娱乐",     "消耗", "▷"),

            new("idle",      "发呆",     "零散", "…"),
        };

        /// <summary>
        /// 旧片代号 → 现在的片。**只做显示与统计的归并，不动数据库一个字节。**
        ///
        /// 为什么必须有这张表：显示名靠 <see cref="ByCode"/> 反查，查不到就退化成显示原始
        /// code（"nap"、"doc"）。合并片时如果直接删清单，历史记录会当场变成英文。
        /// 有了别名，那 4 条记在「午睡」上的睡觉会正常显示成「睡觉」并归到「睡觉」统计；
        /// 哪天想拆回来，改这张表一行就行 —— 无需迁移、可逆。
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Alias =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nap"]      = "sleep",
                ["doc"]      = "work",
                ["meeting"]  = "work",
                ["tidy"]     = "chore",
                ["errand"]   = "chore",
                ["book"]     = "learn",
                ["english"]  = "learn",
                ["leetcode"] = "learn",
                ["chat"]     = "people",
                ["family"]   = "people",
                ["dinner"]   = "people",
                ["video"]    = "entertain",
                ["game"]     = "entertain",
                ["show"]     = "entertain",
                ["scroll"]   = "entertain",
            };

        /// <summary>
        /// 把任意历史 code 归一到现在的 code。
        /// 认识就返回它自己；是别名就返回新片；都不认识就原样返回（调用方自己兜底）。
        /// </summary>
        public static string Canonical(string? code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            if (All.Any(p => string.Equals(p.Code, code, StringComparison.Ordinal))) return code;
            return Alias.TryGetValue(code, out var now) ? now : code;
        }

        /// <summary>按 code 找片。旧 code 也能找到（走别名），所以历史数据不会显示成英文。</summary>
        public static Piece? ByCode(string? code)
        {
            string canon = Canonical(code);
            return All.FirstOrDefault(p => string.Equals(p.Code, canon, StringComparison.Ordinal));
        }

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

        /// <summary>
        /// 一个 code 对应的所有记忆键：它自己 + 归一后的片 + 归到同一个片的旧片。
        ///
        /// 为什么不是简单 `Canonical(code)`：用户可能在合并前就给旧片改过名、钉过、记过归属。
        /// 只读新键会让那些记忆凭空消失（点「午睡」记的归属，合并后点「睡觉」却要重选）。
        /// 所以读的时候把旧键也扫一遍，写的时候一律写新键。
        /// </summary>
        private static IEnumerable<string> MemoryKeys(string code)
        {
            string canon = Pieces.Canonical(code);
            yield return canon;
            if (!string.Equals(canon, code, StringComparison.Ordinal)) yield return code;

            foreach (var kv in Pieces.Alias)
            {
                if (string.Equals(kv.Value, canon, StringComparison.Ordinal)
                    && !string.Equals(kv.Key, code, StringComparison.Ordinal))
                {
                    yield return kv.Key;
                }
            }
        }

        public string DisplayName(string code)
        {
            foreach (string key in MemoryKeys(code))
            {
                if (_state.Names.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom))
                {
                    return custom;
                }
            }
            return Pieces.ByCode(code)?.DefaultName ?? code;
        }

        public bool IsHidden(string code)
        {
            foreach (string key in MemoryKeys(code))
            {
                if (_state.Hidden.Contains(key)) return true;
            }
            return false;
        }

        /// <summary>被隐藏的片有多少个（用于"恢复显示"入口，不给用户留一条回不去路）。</summary>
        public int HiddenCount => _state.Hidden.Count;

        /// <summary>把所有隐藏的片恢复显示。清理清单不该是单向操作。</summary>
        public void UnhideAll()
        {
            _state.Hidden.Clear();
            Save();
        }

        /// <summary>某个片上次用的时长；没有就用 30 分（最常见的一段）。</summary>
        public int DurationFor(string code)
        {
            foreach (string key in MemoryKeys(code))
            {
                if (_state.LastDuration.TryGetValue(key, out int d) && d > 0) return d;
            }
            return 30;
        }

        /// <summary>
        /// 这个片上次归到哪个目标。
        ///
        /// 为什么归属要挂在"片"上而不是全局一个"当前目标"：
        /// 片和目标本来就是强关联的 ——「写代码」几乎总归 PersonalHub，「读书」归另一条线。
        /// 用一次的选择换后面所有次都不用选，且**不增加任何一次点击**。
        ///
        /// 空串有两种含义，靠 <see cref="HasGoalFor"/> 区分：
        ///   有记忆且为空串 = 上次明确选了「不归目标」，这是一次真实的选择，要记住；
        ///   没有记忆       = 从没选过。
        /// 这两个必须分开，否则"不归目标"记不住，下次又被系统塞回某个目标。
        /// </summary>
        public string GoalFor(string code)
        {
            foreach (string key in MemoryKeys(code))
            {
                if (_state.GoalFor.TryGetValue(key, out var id)) return id ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>这个片有没有归属记忆（"明确不归目标"也算有）。</summary>
        public bool HasGoalFor(string code)
            => MemoryKeys(code).Any(k => _state.GoalFor.ContainsKey(k));

        /// <summary>
        /// 记住这个片的归属。空串 = 记住「这个片明确不归任何目标」。
        ///
        /// 为什么空串也要存下来、而不是把记忆删掉：删掉等于"没选过"，
        /// 下次录入就会走兜底、又被归进某个目标 —— 用户明明选过"不归"，
        /// 结果被系统改回去，这就是"我选了但没用"。
        /// 真的要清除记忆用 <see cref="ForgetGoal"/>。
        /// </summary>
        public void SetGoalFor(string code, string goalId)
        {
            _state.GoalFor[Pieces.Canonical(code)] = goalId ?? string.Empty;
            Save();
        }

        /// <summary>彻底忘掉这个片的归属，回到"从没选过"的状态（旧片记忆一起清）。</summary>
        public void ForgetGoal(string code)
        {
            foreach (string key in MemoryKeys(code).ToList())
            {
                _state.GoalFor.Remove(key);
            }
            Save();
        }

        public void SetDuration(string code, int minutes)
        {
            if (minutes <= 0) return;
            _state.LastDuration[Pieces.Canonical(code)] = minutes;
            Save();
        }

        /// <summary>钉住 / 取消钉住。返回 false 表示已经钉满 6 个。</summary>
        public bool TogglePin(string code)
        {
            string canon = Pieces.Canonical(code);

            // 旧片钉过、现在点的是新片：先按"已钉"处理（取消掉它），不要钉成两个。
            string? existing = _state.Pinned.FirstOrDefault(
                c => string.Equals(Pieces.Canonical(c), canon, StringComparison.Ordinal));

            if (existing is not null)
            {
                _state.Pinned.Remove(existing);
                Save();
                return true;
            }

            if (_state.Pinned.Count >= 6) return false;

            _state.Pinned.Add(canon);
            Save();
            return true;
        }

        /// <summary>这个片（含它的旧代号）有没有被钉住。</summary>
        public bool IsPinned(string code)
        {
            string canon = Pieces.Canonical(code);
            return _state.Pinned.Any(c => string.Equals(Pieces.Canonical(c), canon, StringComparison.Ordinal));
        }

        public void Rename(string code, string name)
        {
            string canon = Pieces.Canonical(code);
            if (string.IsNullOrWhiteSpace(name))
            {
                _state.Names.Remove(canon);
            }
            else
            {
                _state.Names[canon] = name.Trim();
            }
            Save();
        }

        public void SetHidden(string code, bool hidden)
        {
            string canon = Pieces.Canonical(code);
            if (hidden) _state.Hidden.Add(canon);
            else _state.Hidden.Remove(canon);
            Save();
        }

        /// <summary>
        /// 录入页要展示的片：钉子优先（顺序即钉的顺序），其余按最近 7 天点击频次排，再按清单顺序兜底。
        /// 频次由调用方传进来（它需要读本地库，不该让偏好设置去碰数据库）。
        ///
        /// 2026-10 清单收缩到 12 个之后，这个列表就是**全部片**，不再有"前排 6 个 + 展开"两层。
        /// 少一次"要不要展开"的判断，而 12 个仍然是一屏能扫完的量。
        /// </summary>
        public List<Piece> FrontRow(IReadOnlyDictionary<string, int> freq7d, int slots = 99)
        {
            var result = new List<Piece>();
            var taken = new HashSet<string>(StringComparer.Ordinal);

            foreach (var code in _state.Pinned)
            {
                // ByCode 走别名：合并前钉的是 "nap"，现在也要认出这是「睡觉」。
                var p = Pieces.ByCode(code);
                if (p is not null && !IsHidden(code) && taken.Add(p.Code)) result.Add(p);
                if (result.Count >= slots) return result;
            }

            var order = Pieces.All
                .Select((p, i) => (p.Code, Index: i))
                .ToDictionary(x => x.Code, x => x.Index, StringComparer.Ordinal);

            // 频次表的键是历史 code（nap / doc / scroll…），要按别名累加到现在的片，
            // 否则合并之后"睡觉"的频次只剩合并后新记的那几次，排序会突然变样。
            var rest = Pieces.All
                .Where(p => !IsHidden(p.Code) && taken.Add(p.Code))
                .Select(p => new
                {
                    Piece = p,
                    Count = freq7d
                        .Where(kv => string.Equals(Pieces.Canonical(kv.Key), p.Code, StringComparison.Ordinal))
                        .Sum(kv => kv.Value),
                })
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

            /// <summary>片 → 上次归到的目标 id。2026-10 新增，旧 JSON 里没有这个键，兜底见构造函数。</summary>
            public Dictionary<string, string> GoalFor { get; set; } = new();
        }

        private static State Load()
        {
            try
            {
                var json = Preferences.Default.Get(Key, string.Empty);
                if (string.IsNullOrWhiteSpace(json)) return new State();

                var state = JsonSerializer.Deserialize<State>(json) ?? new State();

                // 旧版本的 JSON 里没有 GoalFor（以及更早版本没有 LastDuration）。
                // 属性初始化器只在"键缺失"时生效，一旦某次写进了 null 就再也不会被覆盖，
                // 所以这里统一兜一次 —— 一个空引用不该让录入页打不开。
                state.Pinned ??= new();
                state.Hidden ??= new();
                state.Names ??= new();
                state.LastDuration ??= new();
                state.GoalFor ??= new();

                return state;
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
