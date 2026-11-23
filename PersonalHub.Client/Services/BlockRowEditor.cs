using PersonalHub.Client.Controls;
using PersonalHub.Client.Data;

namespace PersonalHub.Client.Services
{
    /// <summary>
    /// 时间块行编辑：改时长 / 换目标 / 改备注 / 删除。
    ///
    /// 为什么抽成静态类：录入页今日列表和 R10 时间块库都要同一套轻编辑，
    /// 各抄一份早晚改岔（改时长加一个自定义档，库页忘了跟）。
    /// 交互不变 —— 还是「···」弹 ActionSheet，逐项改，不跳编辑表单。
    ///
    /// 职责边界：这里只负责"改数据 + 置脏 + 触发上传"，
    /// 改完的界面刷新由调用方的回调负责（两个页面的刷新内容不一样）。
    /// </summary>
    public static class BlockRowEditor
    {
        /// <summary>
        /// 弹行菜单并执行所选操作。
        /// allowDelete=false 时不给「删除」—— 库是回看用的，误删比误改伤；
        /// 录入页（今日列表）保留删除，因为今天记错的当场就该清掉。
        /// onChanged：数据真的改了之后调用（改完刷什么由调用方决定 —— 录入页要刷
        /// 今日列表+统计+备注补全池，库页只刷自己）。返回给用户看的提示文案；null = 用户取消了。
        /// </summary>
        public static async Task<string?> MenuAsync(
            ContentPage page,
            LocalBlock row,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            PieceSettings settings,
            RecordSyncService<LocalBlock> sync,
            bool allowDelete,
            Func<Task>? onChanged = null)
        {
            string name = settings.DisplayName(row.PieceCode);
            string title = $"{name}　{(row.DurationMin > 0 ? FormatMinutes(row.DurationMin) : "未填时长")}";

            var options = new List<string> { "改时长", "换目标", "备注" };
            if (allowDelete) options.Add("删除");

            string pick = await page.DisplayActionSheet(title, "取消", null, options.ToArray());

            string? result = pick switch
            {
                "改时长" => await ChangeDurationAsync(page, row, blocks, sync),
                "换目标" => await ChangeGoalAsync(page, row, blocks, goals, sync),
                "备注" => await ChangeNoteAsync(page, row, blocks, settings, sync),
                "删除" => await DeleteAsync(row, blocks, sync),
                _ => null,
            };

            if (result is not null && onChanged is not null)
            {
                await onChanged();
            }

            return result;
        }

        /// <summary>卡片式多行编辑备注（TextEditorPage），单行弹框只露出尾巴，改旧备注等于盲改。</summary>
        public static async Task<string?> ChangeNoteAsync(
            ContentPage page, LocalBlock row,
            LocalRecordStore<LocalBlock> blocks,
            PieceSettings settings,
            RecordSyncService<LocalBlock> sync)
        {
            string? input = await TextEditorPage.ShowAsync(
                $"备注 · {settings.DisplayName(row.PieceCode)}",
                initial: row.Note,
                placeholder: "比如「理发」「路上顺便买了电池」",
                compact: true,
                maxLength: 200);
            if (input is null) return null;   // 取消 = 不改

            row.Note = input;
            Touch(row);
            await blocks.UpdateAsync(row);
            _ = sync.SyncAsync();

            return string.IsNullOrEmpty(input) ? "已清空备注" : "已更新备注";
        }

        /// <summary>时长：档位直选 + 自定义（1~1440 分钟）。</summary>
        public static async Task<string?> ChangeDurationAsync(
            ContentPage page, LocalBlock row,
            LocalRecordStore<LocalBlock> blocks,
            RecordSyncService<LocalBlock> sync)
        {
            var options = Pieces.Durations.Select(FormatMinutes).ToList();
            options.Add("自定义…");

            string pick = await page.DisplayActionSheet("改成多久", "取消", null, options.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "取消") return null;

            int minutes;
            if (pick == "自定义…")
            {
                string? input = await page.DisplayPromptAsync("自定义时长", "分钟数（1 ~ 1440）",
                    initialValue: row.DurationMin > 0 ? row.DurationMin.ToString() : "30",
                    maxLength: 4, keyboard: Keyboard.Numeric);
                if (input is null) return null;
                if (!int.TryParse(input, out minutes) || minutes < 1 || minutes > 1440)
                    return "时长要在 1 ~ 1440 分钟之间";
            }
            else
            {
                int idx = options.IndexOf(pick);
                if (idx < 0) return null;
                minutes = Pieces.Durations[idx];
            }

            row.DurationMin = minutes;
            Touch(row);
            await blocks.UpdateAsync(row);
            _ = sync.SyncAsync();

            return $"已改成 {FormatMinutes(row.DurationMin)}";
        }

        /// <summary>目标：不归任何目标 / 各进行中目标。</summary>
        public static async Task<string?> ChangeGoalAsync(
            ContentPage page, LocalBlock row,
            LocalRecordStore<LocalBlock> blocks,
            LocalRecordStore<LocalGoal> goals,
            RecordSyncService<LocalBlock> sync)
        {
            var all = await goals.GetByRangeAsync(0, long.MaxValue);
            var active = all.Where(g => g.Status == 0).OrderBy(g => g.Title).ToList();

            var names = new List<string> { "不归任何目标" };
            names.AddRange(active.Select(g => g.Title));

            string? pick = await page.DisplayActionSheet("归到哪个目标", "取消", null, names.ToArray());
            if (string.IsNullOrEmpty(pick) || pick == "取消") return null;

            int idx = names.IndexOf(pick);
            row.GoalId = idx <= 0 ? string.Empty : active[idx - 1].Id;
            Touch(row);
            await blocks.UpdateAsync(row);
            _ = sync.SyncAsync();

            return idx <= 0 ? "已取消目标归属" : $"已归到「{pick}」";
        }

        /// <summary>
        /// 删除：已同步的留墓碑（Deleted=true），否则下次镜像同步又会被拉回来；
        /// 没上传过的直接物理删。只在录入页的今日列表提供。
        /// </summary>
        public static async Task<string?> DeleteAsync(
            LocalBlock row,
            LocalRecordStore<LocalBlock> blocks,
            RecordSyncService<LocalBlock> sync)
        {
            bool ok = await blocks.FindAsync(row.Id) is not null;   // 防御：行已被并发删掉时不弹确认
            if (!ok) return null;

            if (row.Synced)
            {
                row.Deleted = true;
                row.Synced = false;
                row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await blocks.UpdateAsync(row);
            }
            else
            {
                await blocks.DeleteAsync(row.Id);
            }
            _ = sync.SyncAsync();

            return "已删除";
        }

        private static void Touch(LocalBlock row)
        {
            row.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            row.Synced = false;
        }

        /// <summary>与 BlockEntryPage.FormatMinutes 同口径（这里不能引它 —— 页面类不便反向依赖）。</summary>
        public static string FormatMinutes(int minutes)
        {
            if (minutes <= 0) return "0 分钟";
            if (minutes < 60) return $"{minutes} 分钟";

            int h = minutes / 60;
            int m = minutes % 60;
            return m == 0 ? $"{h} 小时" : $"{h} 小时 {m} 分";
        }
    }
}
