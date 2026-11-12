using System;
using System.Collections.Generic;
using QuietMode.Core.Json;
using QuietMode.Core.Maintenance;

namespace QuietMode.Core.Scheduling
{
    /// <summary>
    /// reconcile 链上的一步。模块化（maintenance / disk / fan / verify），
    /// 顺序由 ReconcilePlanner 固定，执行层不允许调整。
    /// </summary>
    public sealed class ReconcileStep
    {
        public int Order;
        /// <summary>maintenance | disk | fan | verify</summary>
        public string Module = "";
        /// <summary>night | day | offline | online | silent | normal | verify</summary>
        public string Action = "";
        public string Detail = "";
        public bool Critical = true;
        /// <summary>pending | done | failed | skipped | notrun</summary>
        public string Status = "pending";
        public string Error = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("order", Order);
            o.Set("module", Module ?? "");
            o.Set("action", Action ?? "");
            o.Set("detail", Detail ?? "");
            o.Set("critical", Critical);
            o.Set("status", Status ?? "pending");
            o.Set("error", Error ?? "");
            return o;
        }

        public static ReconcileStep FromJson(JsonObject o)
        {
            ReconcileStep s = new ReconcileStep();
            s.Order = o.GetInt("order", 0);
            s.Module = o.GetString("module", "");
            s.Action = o.GetString("action", "");
            s.Detail = o.GetString("detail", "");
            s.Critical = o.GetBool("critical", true);
            s.Status = o.GetString("status", "pending");
            s.Error = o.GetString("error", "");
            return s;
        }

        public string ToLine()
        {
            string head = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,2}. {1,-11} {2,-8} {3}", Order, Module, Action, Detail);
            switch ((Status ?? "").ToLowerInvariant())
            {
                case "done": head += "  [完成]"; break;
                case "failed": head += "  [失败] " + Error; break;
                case "skipped": head += "  [跳过] " + Error; break;
                case "notrun": head += "  [未执行] " + Error; break;
                default: head += "  [待执行]"; break;
            }
            return head;
        }
    }

    /// <summary>
    /// 一次 reconcile 的完整计划。夜间和白天各有一条固定顺序，来自规格 §4.2 / §4.3：
    ///   夜间：维护 → 磁盘脱机 → 风扇静音 → 验证
    ///   白天：磁盘联机 → 维护恢复 → 风扇常态 → 验证
    /// </summary>
    public sealed class ReconcilePlan
    {
        /// <summary>night | day | restore | verify</summary>
        public string Action = "";
        public string Trigger = "";
        public string Reason = "";
        public string CreatedUtc = "";
        public List<ReconcileStep> Steps = new List<ReconcileStep>();
        public List<string> Notes = new List<string>();
        /// <summary>夜间/白天要执行的维护步骤明细（交给 worker 原样执行）。</summary>
        public MaintenancePlan Maintenance;

        public ReconcileStep Add(string module, string action, string detail, bool critical)
        {
            ReconcileStep s = new ReconcileStep();
            s.Order = Steps.Count + 1;
            s.Module = module;
            s.Action = action;
            s.Detail = detail ?? "";
            s.Critical = critical;
            Steps.Add(s);
            return s;
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("action", Action ?? "");
            o.Set("trigger", Trigger ?? "");
            o.Set("reason", Reason ?? "");
            o.Set("createdUtc", CreatedUtc ?? "");
            JsonArray arr;
            o.SetArray("steps", out arr);
            foreach (ReconcileStep s in Steps) arr.Add(s.ToJson());
            o.SetArray("notes", out arr);
            foreach (string n in Notes) arr.Add(n);
            if (Maintenance != null) o.Set("maintenance", Maintenance.ToJson());
            return o;
        }

        public string ToText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("计划 " + Action + "（触发：" + Trigger + "）：" + Reason);
            foreach (ReconcileStep s in Steps) sb.AppendLine("  " + s.ToLine());
            foreach (string n in Notes) sb.AppendLine("  说明  " + n);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>worker 返回的单步结果。</summary>
    public sealed class ReconcileStepResult
    {
        public int Order;
        public string Module = "";
        public string Action = "";
        public string Status = "pending";
        public string Error = "";
        public string Detail = "";

        public static ReconcileStepResult FromJson(JsonObject o)
        {
            ReconcileStepResult r = new ReconcileStepResult();
            r.Order = o.GetInt("order", 0);
            r.Module = o.GetString("module", "");
            r.Action = o.GetString("action", "");
            r.Status = o.GetString("status", "pending");
            r.Error = o.GetString("error", "");
            r.Detail = o.GetString("detail", "");
            return r;
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("order", Order);
            o.Set("module", Module ?? "");
            o.Set("action", Action ?? "");
            o.Set("status", Status ?? "");
            o.Set("error", Error ?? "");
            o.Set("detail", Detail ?? "");
            return o;
        }
    }

    public sealed class DiskStateObservation
    {
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public int DiskNumber = -1;
        public bool Matched;
        /// <summary>null = 读不到（权限或 WMI 失败），既不算在线也不算脱机。</summary>
        public bool? IsOffline;
        public string Error = "";

        public string Display
        {
            get
            {
                string n = string.IsNullOrEmpty(FriendlyName) ? (UniqueId.Length > 8 ? UniqueId.Substring(0, 8) : UniqueId) : FriendlyName;
                return n + "（#" + DiskNumber + "）";
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("serialNumber", SerialNumber ?? "");
            o.Set("friendlyName", FriendlyName ?? "");
            o.Set("diskNumber", DiskNumber);
            o.Set("matched", Matched);
            o.Set("isOffline", IsOffline.HasValue ? (JsonNode)new JsonBool(IsOffline.Value) : JsonNull.Instance);
            o.Set("error", Error ?? "");
            return o;
        }
    }

    /// <summary>
    /// 硬盘空闲停转（DISKIDLE）的观测。配置启用时它是 reconcile 链的一环：
    /// 计划负责写、验证负责回读，两边都不许拿「命令返回 0」当成功。
    /// </summary>
    public sealed class SpinDownObservation
    {
        public bool Enabled;
        public int ExpectedSeconds;
        public int AcSeconds = -1;
        public int DcSeconds = -1;
        public bool Captured;
        public string Error = "";

        /// <summary>DC 只在读得到时才参与判定（台式机上可能没有电池分支）。</summary>
        public bool Matches
        {
            get
            {
                if (!Captured) return false;
                if (AcSeconds != ExpectedSeconds) return false;
                if (DcSeconds >= 0 && DcSeconds != ExpectedSeconds) return false;
                return true;
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("enabled", Enabled);
            o.Set("expectedSeconds", ExpectedSeconds);
            o.Set("acSeconds", AcSeconds);
            o.Set("dcSeconds", DcSeconds);
            o.Set("captured", Captured);
            o.Set("matches", Matches);
            o.Set("error", Error ?? "");
            return o;
        }
    }

    public sealed class FanObservation
    {
        public bool Available;
        public string CurrentProfile = "";
        public string ExpectedProfile = "";
        public string Error = "";
        public bool Matches { get { return Available && CurrentProfile.Length > 0
                                            && string.Equals(CurrentProfile, ExpectedProfile, StringComparison.OrdinalIgnoreCase); } }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("available", Available);
            o.Set("currentProfile", CurrentProfile ?? "");
            o.Set("expectedProfile", ExpectedProfile ?? "");
            o.Set("error", Error ?? "");
            return o;
        }
    }

    public enum ReconcileVerdict
    {
        Unknown = 0,
        Ok,
        Degraded,
        Failed
    }

    /// <summary>
    /// 全部组件验证完才叫成功（规格 §7.4）。缺一项就是降级或失败，
    /// 绝不符合「worker 说成功」就报成功。
    ///
    /// 除了 verdict（好不好），还给出 classification（要不要动手、能不能修）。
    /// 这两件事必须分开：一个读不到的磁盘是 Degraded，但重跑一百遍也还是读不到。
    /// </summary>
    public sealed class ReconcileVerification
    {
        public string Action = "";
        public ReconcileVerdict Verdict = ReconcileVerdict.Unknown;
        public DriftClass Classification = DriftClass.Compliant;
        public List<string> Problems = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Notes = new List<string>();
        /// <summary>结构化偏差清单：Verifier 写、控制器读，中间不靠解析中文。</summary>
        public List<DriftItem> Items = new List<DriftItem>();
        public MaintenanceVerification Maintenance;
        public List<DiskStateObservation> Disks = new List<DiskStateObservation>();
        public SpinDownObservation SpinDown;
        public FanObservation Fan;

        /// <summary>记录一条「目标状态没达成」。fixable 表示重跑当前相位能不能修好。</summary>
        public void Problem(string component, bool fixable, string target, string message)
        {
            Problems.Add(message);
            Items.Add(new DriftItem(component, DriftKind.Mismatch, fixable, target, message));
        }

        /// <summary>记录一条「读不到」。永远不可修复 —— 未知不是偏差。</summary>
        public void Unknown(string component, string message)
        {
            Warnings.Add(message);
            Items.Add(new DriftItem(component, DriftKind.Unknown, false, "", message));
        }

        /// <summary>
        /// 记录一条「达成但有隐患」的偏差。fixable 表示重跑当前相位是否值得再试一次；
        /// 与 Problem 的区别只是严重度（Degraded vs Failed），不是「要不要修」。
        /// </summary>
        public void Warning(string component, bool fixable, string target, string message)
        {
            Warnings.Add(message);
            Items.Add(new DriftItem(component, DriftKind.Mismatch, fixable, target, message));
        }

        /// <summary>记录一条安全阻塞。整轮禁止自动写入。</summary>
        public void Safety(string component, string message)
        {
            Problems.Add(message);
            Items.Add(new DriftItem(component, DriftKind.Safety, false, "", message));
        }

        public void Settle()
        {
            if (Problems.Count > 0) Verdict = ReconcileVerdict.Failed;
            else if (Warnings.Count > 0) Verdict = ReconcileVerdict.Degraded;
            else Verdict = ReconcileVerdict.Ok;
            Classification = DriftClassifier.Classify(Items);
        }

        public int FixableCount
        {
            get
            {
                int n = 0;
                foreach (DriftItem i in Items) if (i != null && i.Fixable) n++;
                return n;
            }
        }

        public string VerdictText
        {
            get
            {
                switch (Verdict)
                {
                    case ReconcileVerdict.Ok: return Action == "night" ? "NightActive" : "DayActive";
                    case ReconcileVerdict.Degraded: return "NightDegraded";
                    case ReconcileVerdict.Failed: return "Failed";
                    default: return "Unknown";
                }
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("action", Action ?? "");
            o.Set("verdict", Verdict.ToString());
            o.Set("verdictText", VerdictText);
            o.Set("classification", Classification.ToString());
            o.Set("classificationText", DriftClassifier.Text(Classification));
            JsonArray arr;
            o.SetArray("problems", out arr);
            foreach (string s in Problems) arr.Add(s);
            o.SetArray("warnings", out arr);
            foreach (string s in Warnings) arr.Add(s);
            o.SetArray("notes", out arr);
            foreach (string s in Notes) arr.Add(s);
            o.SetArray("drift", out arr);
            foreach (DriftItem i in Items)
            {
                JsonObject d = new JsonObject();
                d.Set("component", i.Component ?? "");
                d.Set("kind", i.Kind.ToString());
                d.Set("fixable", i.Fixable);
                d.Set("target", i.Target ?? "");
                d.Set("message", i.Message ?? "");
                arr.Add(d);
            }
            if (Maintenance != null) o.Set("maintenance", Maintenance.ToJson());
            o.SetArray("disks", out arr);
            foreach (DiskStateObservation d in Disks) arr.Add(d.ToJson());
            if (SpinDown != null) o.Set("spinDown", SpinDown.ToJson());
            if (Fan != null) o.Set("fan", Fan.ToJson());
            return o;
        }

        public string ToText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("验证: " + VerdictText + "（" + Action + "）");
            if (Classification != DriftClass.Compliant)
                sb.AppendLine("分类: " + DriftClassifier.Text(Classification) + "（需修复 " + FixableCount + " 项）");
            foreach (string s in Problems) sb.AppendLine("  失败  " + s);
            foreach (string s in Warnings) sb.AppendLine("  降级  " + s);
            foreach (string s in Notes) sb.AppendLine("  说明  " + s);
            return sb.ToString().TrimEnd();
        }
    }
}
