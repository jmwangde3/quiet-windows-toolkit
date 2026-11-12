using System;
using System.Collections.Generic;

namespace QuietMode.Core.Scheduling
{
    /// <summary>一条偏差的性质。决定「要不要动手」，而不是「好不好看」。</summary>
    public enum DriftKind
    {
        /// <summary>目标状态没达成，且我们知道期望值是什么。</summary>
        Mismatch = 0,
        /// <summary>读不到，既不能算达成也不能算没达成。</summary>
        Unknown,
        /// <summary>存在数据或设备安全风险，任何自动写入都必须停手。</summary>
        Safety
    }

    /// <summary>
    /// 偏差的最终分类。这是「验证」和「修复」之间唯一的接口：
    /// 验证只负责说出事实，分类负责决定下一步，控制器不再靠 verdict 字符串猜。
    /// </summary>
    public enum DriftClass
    {
        /// <summary>完全符合期望，一个字节都不写。</summary>
        Compliant = 0,
        /// <summary>有偏差，且重跑当前相位能够修好 —— 值得立刻动手。</summary>
        ActionableDrift,
        /// <summary>有偏差，但自动重跑修不好（例如权限被拒、写入被策略挡住）。记录并等下个周期。</summary>
        UnfixableDrift,
        /// <summary>只有「读不到」，没有任何可确认的偏差 —— 不许盲目写入，也不许当成功。</summary>
        ObservationUnknown,
        /// <summary>安全闸门拦住了：身份不明、占用未确认等。绝不自动写入。</summary>
        SafetyBlocked
    }

    public sealed class DriftItem
    {
        /// <summary>maintenance | disk | spindown | fan</summary>
        public string Component = "";
        public DriftKind Kind = DriftKind.Mismatch;
        /// <summary>重跑当前相位能不能修好这条偏差。</summary>
        public bool Fixable;
        /// <summary>出问题的对象（任务全名 / 磁盘 uniqueId / 配置项），便于定点排查。</summary>
        public string Target = "";
        public string Message = "";

        public DriftItem() { }

        public DriftItem(string component, DriftKind kind, bool fixable, string target, string message)
        {
            Component = component ?? "";
            Kind = kind;
            Fixable = fixable;
            Target = target ?? "";
            Message = message ?? "";
        }

        public string ToLine()
        {
            string tag = Kind == DriftKind.Safety ? "安全" : (Kind == DriftKind.Unknown ? "未知" : (Fixable ? "可修复" : "需人工"));
            return "[" + tag + "] " + (Component.Length > 0 ? Component + "：" : "") + Message;
        }
    }

    public static class DriftClassifier
    {
        /// <summary>
        /// 排序原则（先安全，再可修，再未知）：
        ///   SafetyBlocked &gt; ActionableDrift &gt; UnfixableDrift &gt; ObservationUnknown &gt; Compliant
        /// 一条 Safety 就足以让整轮不许写；一条可修复偏差就值得重跑，哪怕同时还有读不到的项目。
        /// </summary>
        public static DriftClass Classify(IList<DriftItem> items)
        {
            if (items == null || items.Count == 0) return DriftClass.Compliant;

            bool fixable = false, unfixable = false, unknown = false;
            foreach (DriftItem i in items)
            {
                if (i == null) continue;
                switch (i.Kind)
                {
                    case DriftKind.Safety: return DriftClass.SafetyBlocked;
                    case DriftKind.Unknown: unknown = true; break;
                    default:
                        if (i.Fixable) fixable = true; else unfixable = true;
                        break;
                }
            }
            if (fixable) return DriftClass.ActionableDrift;
            if (unfixable) return DriftClass.UnfixableDrift;
            if (unknown) return DriftClass.ObservationUnknown;
            return DriftClass.Compliant;
        }

        public static string Text(DriftClass c)
        {
            switch (c)
            {
                case DriftClass.Compliant: return "符合";
                case DriftClass.ActionableDrift: return "可修复偏差";
                case DriftClass.UnfixableDrift: return "需人工的偏差";
                case DriftClass.ObservationUnknown: return "状态未知";
                case DriftClass.SafetyBlocked: return "安全阻塞";
                default: return "未知";
            }
        }

        /// <summary>
        /// 只有「可修复偏差」允许自动重跑。未知与安全阻塞都不许盲目写入：
        /// 前者可能把正常状态改坏，后者可能动到不该动的设备。
        /// </summary>
        public static bool AllowsAutomaticRepair(DriftClass c)
        {
            return c == DriftClass.ActionableDrift;
        }
    }
}
