using QuietMode.Windows;
using QuietMode.Windows.Native;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// SchedulerStatus 的判定与文案。全是属性计算，不初始化 COM，也不读真实任务。
    ///
    /// 这批用例的由来：2026-11-14 14:07 第一次实机回滚，v2 任务已删、v1 两个闸门按设计
    /// 重新启用，`qmctl task status` 却打出「结论：存在重复调度（v1 闸门仍在启用）」并返回
    /// 退出码 2。没有 v2 就没有「重复」可言，那句话会把一次成功的回滚描述成失败。
    /// 同一条 Conflict 还被 Uninstall-QuietMode.ps1 用来判断「删除后是否还有调度器」，
    /// 于是每次正常卸载都会多出一条假 WARN。
    /// </summary>
    public static partial class Program
    {
        private static void RunSchedulerStatusTests()
        {
            Run("调度状态：v2 在位 + v1 全停 → 无冲突", TestSchedulerStatusNormal);
            Run("调度状态：v2 在位 + v1 启用 → 报重复", TestSchedulerStatusConflict);
            Run("调度状态：v2 不存在 + v1 启用 → 回滚态，不算冲突", TestSchedulerStatusRolledBack);
            Run("调度状态：v2 不存在 + v1 全停 → 当前没有调度器", TestSchedulerStatusEmpty);
            Run("调度状态：v2 读不到 + v1 启用 → 保守按冲突处理", TestSchedulerStatusUnknownV2);
            Run("调度状态：v1 存在但启用位读不到 → 报「无法确认」", TestSchedulerStatusUnknownLegacy);
        }

        /// <summary>造一个不碰 COM 的 SchedulerStatus：只有 Exists 与若干 v1 闸门的启用位。</summary>
        private static SchedulerStatus MakeStatus(bool? exists, params bool?[] legacyEnabled)
        {
            SchedulerStatus s = new SchedulerStatus();
            s.Exists = exists;
            for (int i = 0; i < legacyEnabled.Length; i++)
            {
                TaskQueryResult t = new TaskQueryResult();
                t.Exists = true;
                t.Enabled = legacyEnabled[i];
                s.Legacy.Add(t);
                s.LegacyNames.Add("\\QuietHDD\\Gate" + i);
            }
            return s;
        }

        private static void TestSchedulerStatusNormal()
        {
            SchedulerStatus s = MakeStatus(true, false, false);
            True(!s.Conflict, "v1 全停用不应报冲突");
            True(!s.LegacyActive, "LegacyActive 应为 false");
            True(s.ToText().Contains("没有检测到重复调度"), "文案应为「没有检测到重复调度」");
        }

        private static void TestSchedulerStatusConflict()
        {
            SchedulerStatus s = MakeStatus(true, true, false);
            True(s.Conflict, "v1 有启用项且 v2 在位 → 必须报冲突");
            True(s.LegacyActive, "LegacyActive 应为 true");
            True(s.ToText().Contains("存在重复调度"), "文案应为「存在重复调度」");
        }

        private static void TestSchedulerStatusRolledBack()
        {
            SchedulerStatus s = MakeStatus(false, true, true);
            True(!s.Conflict, "v2 不存在时再谈重复调度就是误报");
            True(s.LegacyActive, "回滚态下 v1 确实是启用状态");
            string t = s.ToText();
            True(t.Contains("v2 未注册"), "应说明 v2 未注册：" + t);
            True(t.Contains("回滚态"), "应标出这是回滚态：" + t);
            True(!t.Contains("存在重复调度"), "回滚态不得出现「存在重复调度」：" + t);
        }

        private static void TestSchedulerStatusEmpty()
        {
            SchedulerStatus s = MakeStatus(false, false, false);
            True(!s.Conflict, "两个都没有，谈不上冲突");
            True(!s.LegacyActive, "LegacyActive 应为 false");
            string t = s.ToText();
            True(t.Contains("当前没有调度器"), "应说明当前没有调度器：" + t);
        }

        private static void TestSchedulerStatusUnknownV2()
        {
            SchedulerStatus s = MakeStatus(null, true);
            True(s.Conflict, "v2 状态未知时必须保守，按可能有冲突处理");
        }

        private static void TestSchedulerStatusUnknownLegacy()
        {
            // 注意 params 陷阱：MakeStatus(true, null) 会被绑定成「null 数组」而不是
            // 「一个值为 null 的元素」，因此这里显式写数组。
            SchedulerStatus s = MakeStatus(true, new bool?[] { null });
            True(s.Conflict, "启用位读不到时不得当成「已停用」");
            True(s.LegacyUnknown, "LegacyUnknown 应为 true");
            True(s.ToText().Contains("无法确认"), "文案应为「无法确认」：" + s.ToText());
        }
    }
}
