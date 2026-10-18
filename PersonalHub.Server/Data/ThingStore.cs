using System;
using System.Collections.Generic;
using System.Linq;
using Personalhub;
using personalHub.Server.Analysis;

namespace personalHub.Server.Data
{
    /// <summary>
    /// 事件反思存储层 —— 与 <see cref="RecordStore"/> 对称，共用同一个 <see cref="Db"/> 连接（同一个库文件）。
    ///
    /// 账单和事件反思是同一个人仓库里的两类数据，备份/迁移时希望是一个文件带走。
    /// R1 只加了 updated_at / deleted 两列（为将来"电脑端改反思"留位），
    /// 查询已经过滤软删，所以以后补编辑功能不需要再改表。
    /// </summary>
    public sealed class ThingStore
    {
        private readonly Db _db;

        public ThingStore(Db db)
        {
            _db = db;
        }

        public string DbPath => _db.Path;

        /// <summary>
        /// 幂等写入：同 id 覆盖。返回处理条数。
        /// 客户端同步会被重复触发（切前台、网络恢复），靠这里保证不会写重。
        /// </summary>
        public int Upsert(IEnumerable<Thing> things)
        {
            var list = things as IList<Thing> ?? things.ToList();
            if (list.Count == 0) return 0;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            lock (_db.Sync)
            {
                _db.Conn.RunInTransaction(() =>
                {
                    foreach (var t in list)
                    {
                        var row = ToRow(t);
                        if (row.UpdatedAt == 0) row.UpdatedAt = now;

                        // 已软删的反思，不允许被重复上传"复活"
                        var old = _db.Conn.Find<ThingRow>(row.Id);
                        if (old is not null) row.Deleted = old.Deleted;

                        _db.Conn.InsertOrReplace(row);
                    }
                });
            }

            return list.Count;
        }

        /// <summary>全部未删除的事件反思。</summary>
        public IReadOnlyList<Thing> All()
        {
            lock (_db.Sync)
            {
                return _db.Conn.Table<ThingRow>()
                    .Where(t => t.Deleted == 0)
                    .ToList()
                    .Select(ToProto)
                    .ToList();
            }
        }

        /// <summary>某个自然日（固定 UTC+8，与统计口径一致）的事件反思，按发生时间倒序。</summary>
        public IReadOnlyList<Thing> ByDay(DateTimeOffset day)
        {
            var start = Period.StartOfDay(day);
            long startMs = start.ToUnixTimeMilliseconds();
            long endMs = start.AddDays(1).ToUnixTimeMilliseconds();

            lock (_db.Sync)
            {
                return _db.Conn.Table<ThingRow>()
                    .Where(t => t.Deleted == 0 && t.OccurredAt >= startMs && t.OccurredAt < endMs)
                    .ToList()
                    .OrderByDescending(t => t.OccurredAt)
                    .Select(ToProto)
                    .ToList();
            }
        }

        private static ThingRow ToRow(Thing t) => new()
        {
            Id = t.Id,
            Title = t.Title,
            Tag = t.Tag,
            Emotion = t.Emotion,
            OccurredAt = t.OccurredAt,
            ObjectiveDescription = t.ObjectiveDescription,
            InitialThought = t.InitialThought,
            ActionTaken = t.ActionTaken,
            FinalResult = t.FinalResult,
            Reflection = t.Reflection,
            ActionPlan = t.ActionPlan,
            CreatedAt = t.CreatedAt,
        };

        private static Thing ToProto(ThingRow row) => new()
        {
            Id = row.Id,
            Title = row.Title ?? string.Empty,
            Tag = row.Tag ?? string.Empty,
            Emotion = row.Emotion ?? string.Empty,
            OccurredAt = row.OccurredAt,
            ObjectiveDescription = row.ObjectiveDescription ?? string.Empty,
            InitialThought = row.InitialThought ?? string.Empty,
            ActionTaken = row.ActionTaken ?? string.Empty,
            FinalResult = row.FinalResult ?? string.Empty,
            Reflection = row.Reflection ?? string.Empty,
            ActionPlan = row.ActionPlan ?? string.Empty,
            CreatedAt = row.CreatedAt,
        };
    }
}
