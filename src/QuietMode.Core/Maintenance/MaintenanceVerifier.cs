using System;
using System.Collections.Generic;
using System.Globalization;
using QuietMode.Core.Json;
using QuietMode.Core.Models;

namespace QuietMode.Core.Maintenance
{
    public enum MaintenanceVerdict
    {
        Unknown = 0,
        Ok = 1,
        Degraded = 2,
        Failed = 3
    }

    /// <summary>
    /// 验证结论。Problems = 目标状态没达成（失败）；Warnings = 达成了但有隐患（降级）；
    /// Notes = 事实说明，不影响结论。绝不把「没报错」当成「成功」。
    /// </summary>
    public sealed class MaintenanceVerification
    {
        public string Phase = "";
        public MaintenanceVerdict Verdict = MaintenanceVerdict.Unknown;
        public List<string> Problems = new List<string>();
        public List<string> Warnings = new List<string>();
        public List<string> Notes = new List<string>();

        public string VerdictText
        {
            get
            {
                switch (Verdict)
                {
                    case MaintenanceVerdict.Ok: return Phase == "night" ? "NightActive" : "DayActive";
                    case MaintenanceVerdict.Degraded: return "Degraded";
                    case MaintenanceVerdict.Failed: return "Failed";
                    default: return "Unknown";
                }
            }
        }

        public void Settle()
        {
            if (Problems.Count > 0) Verdict = MaintenanceVerdict.Failed;
            else if (Warnings.Count > 0) Verdict = MaintenanceVerdict.Degraded;
            else Verdict = MaintenanceVerdict.Ok;
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("phase", Phase ?? "");
            o.Set("verdict", Verdict.ToString());
            o.Set("verdictText", VerdictText);
            JsonArray arr;
            o.SetArray("problems", out arr);
            foreach (string s in Problems) arr.Add(s);
            o.SetArray("warnings", out arr);
            foreach (string s in Warnings) arr.Add(s);
            o.SetArray("notes", out arr);
            foreach (string s in Notes) arr.Add(s);
            return o;
        }

        public string ToText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("结论: " + VerdictText + "（" + Phase + "）");
            foreach (string s in Problems) sb.AppendLine("  失败  " + s);
            foreach (string s in Warnings) sb.AppendLine("  降级  " + s);
            foreach (string s in Notes) sb.AppendLine("  说明  " + s);
            return sb.ToString().TrimEnd();
        }
    }

    public static class MaintenanceVerifier
    {
        public static MaintenanceVerification VerifyNight(QuietModeConfig cfg, SystemObservation obs)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (obs == null) obs = new SystemObservation();

            MaintenanceVerification v = new MaintenanceVerification();
            v.Phase = "night";
            MaintenanceSettings m = cfg.Maintenance;

            if (!m.Enabled)
            {
                v.Notes.Add("maintenance.enabled=false：夜间不要求任何禁用，视为通过");
                v.Settle();
                return v;
            }

            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                ObservedTask t = obs.FindTask(def.Path, def.Name);
                if (t == null || t.Presence == TaskPresence.NotPresent)
                {
                    v.Notes.Add(def.FullName + "：不存在（NotPresent），不参与验证");
                    continue;
                }
                if (t.Presence == TaskPresence.Unknown)
                {
                    v.Warnings.Add(def.FullName + "：状态读取失败，无法确认已禁用（"
                                   + (t.Error ?? "").Trim() + "）");
                    continue;
                }
                if (!t.Enabled.HasValue)
                {
                    v.Warnings.Add(def.FullName + "：读不到 Enabled，无法确认已禁用");
                }
                else if (t.Enabled.Value)
                {
                    v.Problems.Add(def.FullName + "：仍然处于启用状态");
                }
                if (t.Running)
                {
                    v.Warnings.Add(def.FullName + "：任务仍在运行（State=Running），维护闸门未完全生效");
                }
            }

            if (m.DisableSysMainAtNight)
            {
                ObservedService svc = obs.FindService(MaintenanceCatalog.SysMainService);
                if (svc == null || !svc.Exists)
                {
                    v.Notes.Add("SysMain：不存在，跳过验证");
                }
                else
                {
                    if (svc.StartValue != 4)
                    {
                        v.Problems.Add("SysMain：启动类型不是 Disabled（Start="
                                       + svc.StartValue.ToString(CultureInfo.InvariantCulture) + "）");
                    }
                    if (svc.Running)
                    {
                        v.Warnings.Add("SysMain：服务仍在运行");
                    }
                }
            }

            CheckRegistryValue(v, obs, MaintenanceCatalog.MaintenanceBoundaryValue,
                m.MaintenanceHour.ToString(CultureInfo.InvariantCulture), "夜间 MaintenanceBoundary");
            if (m.DisableWake)
            {
                CheckRegistryValue(v, obs, MaintenanceCatalog.MaintenanceWakeValue, "0", "夜间 WakeUp");
            }

            v.Settle();
            return v;
        }

        public static MaintenanceVerification VerifyDay(QuietModeConfig cfg, BaselineSnapshot baseline, SystemObservation obs)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            if (obs == null) obs = new SystemObservation();
            if (baseline == null) baseline = new BaselineSnapshot();

            MaintenanceVerification v = new MaintenanceVerification();
            v.Phase = "day";
            MaintenanceSettings m = cfg.Maintenance;

            if (!m.Enabled)
            {
                v.Notes.Add("maintenance.enabled=false：不要求恢复，视为通过");
                v.Settle();
                return v;
            }

            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                TaskSnapshot b = MaintenancePlanner.FindTask(baseline, def.Path, def.Name);
                ObservedTask t = obs.FindTask(def.Path, def.Name);

                if (b == null || !b.Enabled.HasValue)
                {
                    v.Notes.Add(def.FullName + "：baseline 未记录启用状态，不参与验证");
                    continue;
                }
                if (t == null || t.Presence == TaskPresence.NotPresent)
                {
                    if (b.Exists == true) v.Warnings.Add(def.FullName + "：baseline 记录存在，但现在找不到");
                    else v.Notes.Add(def.FullName + "：原本就不存在");
                    continue;
                }
                if (!t.Enabled.HasValue)
                {
                    v.Warnings.Add(def.FullName + "：读不到 Enabled，无法确认已恢复");
                    continue;
                }
                if (t.Enabled.Value != b.Enabled.Value)
                {
                    v.Problems.Add(def.FullName + "：启用状态未恢复到 baseline（期望 "
                                   + (b.Enabled.Value ? "启用" : "禁用") + "，实际 "
                                   + (t.Enabled.Value ? "启用" : "禁用") + "）");
                }
            }

            if (m.DisableSysMainAtNight)
            {
                ServiceSnapshot b = MaintenancePlanner.FindService(baseline, MaintenanceCatalog.SysMainService);
                ObservedService now = obs.FindService(MaintenanceCatalog.SysMainService);
                if (b == null)
                {
                    v.Notes.Add("SysMain：baseline 未记录，不参与验证");
                }
                else if (now == null || !now.Exists)
                {
                    v.Warnings.Add("SysMain：baseline 记录存在，但现在找不到");
                }
                else
                {
                    string desired = MaintenancePlanner.DesiredStartValue(b);
                    if (desired.Length > 0 && now.StartValue.ToString(CultureInfo.InvariantCulture) != desired)
                    {
                        v.Problems.Add("SysMain：启动类型未恢复（期望 Start=" + desired
                                       + "，实际 " + now.StartValue.ToString(CultureInfo.InvariantCulture) + "）");
                    }
                    if (string.Equals(b.Status, "Running", StringComparison.OrdinalIgnoreCase) && !now.Running)
                    {
                        v.Warnings.Add("SysMain：baseline 记录当时在运行，当前未运行");
                    }
                }
            }

            foreach (RegistrySnapshot b in baseline.Registry)
            {
                if (!string.Equals(b.KeyPath, MaintenanceCatalog.MaintenanceRegistryKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool inScope =
                    string.Equals(b.ValueName, MaintenanceCatalog.MaintenanceBoundaryValue, StringComparison.OrdinalIgnoreCase)
                    || (m.DisableWake && string.Equals(b.ValueName, MaintenanceCatalog.MaintenanceWakeValue, StringComparison.OrdinalIgnoreCase));
                if (!inScope) continue;

                ObservedRegistryValue r = obs.FindRegistry(b.KeyPath, b.ValueName);
                string label = b.ValueName;
                if (r == null)
                {
                    v.Warnings.Add(label + "：未探测，无法确认已恢复");
                    continue;
                }
                if (b.Exists)
                {
                    if (!r.Exists)
                    {
                        v.Problems.Add(label + "：baseline 记录存在，当前不存在");
                    }
                    else if (!string.Equals(Normalize(r.Value), Normalize(b.Value), StringComparison.OrdinalIgnoreCase))
                    {
                        v.Problems.Add(label + "：值未恢复（期望 " + b.Value + "，实际 " + r.Value + "）");
                    }
                }
                else
                {
                    if (r.Exists)
                    {
                        v.Problems.Add(label + "：baseline 记录该值原本不存在，当前仍然存在");
                    }
                }
            }

            v.Settle();
            return v;
        }

        private static void CheckRegistryValue(MaintenanceVerification v, SystemObservation obs,
                                               string valueName, string expected, string label)
        {
            ObservedRegistryValue r = obs.FindRegistry(MaintenanceCatalog.MaintenanceRegistryKey, valueName);
            if (r == null || !r.Exists)
            {
                v.Problems.Add(label + "：不存在或未探测（期望 " + expected + "）");
                return;
            }
            if (!string.Equals(Normalize(r.Value), Normalize(expected), StringComparison.OrdinalIgnoreCase))
            {
                v.Problems.Add(label + "：值为 " + r.Value + "，期望 " + expected);
            }
        }

        private static string Normalize(string s)
        {
            return (s ?? "").Trim();
        }
    }
}
