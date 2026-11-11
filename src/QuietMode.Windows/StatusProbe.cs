using System;
using System.Collections.Generic;
using System.Security.Principal;
using QuietMode.Core.Configuration;
using QuietMode.Core.Disks;
using QuietMode.Core.Models;
using QuietMode.Core.Scheduling;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    /// <summary>
    /// 只读状态汇总。托盘 / 设置程序只调它一次就能拿到渲染所需的全部事实，
    /// 不需要（也不允许）自己去调 WMI、注册表或 FanControl。
    ///
    /// 这里的每个字段都是「观测到的事实」或「已存状态」，不含任何成功/失败结论：
    /// 是否达成期望由 MaintenanceVerifier / ReconcileController 判定，界面照抄即可。
    /// </summary>
    public static class StatusProbe
    {
        public static StatusSnapshot Capture(QuietModePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            StatusSnapshot s = new StatusSnapshot();
            s.GeneratedUtc = SystemClock.NowIso();
            s.Elevated = IsElevated();

            QuietModeConfig cfg = LoadConfig(paths, s);
            s.Schedule = cfg.Schedule;
            s.Startup = cfg.Startup;
            try { s.ConfigExists = System.IO.File.Exists(paths.ConfigPath); }
            catch { s.ConfigExists = false; }

            s.State = new StateStore(paths).Load();
            s.Window = ScheduleWindow.Evaluate(cfg.Schedule, DateTime.Now, SystemClock.Uptime());

            DecisionInput input = new DecisionInput();
            input.Schedule = cfg.Schedule;
            input.State = s.State;
            input.Window = s.Window;
            input.Trigger = "status";
            input.LastRunFailed = StateMachine.IsLastRunFailed(s.State);
            s.Decision = StateMachine.Decide(input);

            s.Fan = ReadFan(cfg.Fan);
            s.Disks = ReadDisks(cfg.Disks);
            s.SpinDown = ReadSpinDown(cfg.SpinDown);

            try
            {
                SchedulerStatus st = SchedulerInstaller.Inspect();
                s.Task.Path = st.TaskPath;
                s.Task.Name = st.TaskName;
                s.Task.Exists = st.Exists;
                s.Task.State = st.State ?? "";
                s.Task.LastRunTime = st.LastRunTime ?? "";
                s.Task.LastResult = st.LastResult ?? "";
                s.Task.Conflict = st.Conflict;
                s.Task.Error = st.Error ?? "";
                if (st.LegacyNames != null)
                {
                    List<string> on = new List<string>();
                    for (int i = 0; i < st.LegacyNames.Count; i++)
                    {
                        TaskQueryResult t = st.Legacy != null && i < st.Legacy.Count ? st.Legacy[i] : null;
                        if (t == null) continue;
                        if (!t.Exists.HasValue) on.Add(st.LegacyNames[i] + "=?");
                        else if (t.Exists.Value && t.Enabled.HasValue && t.Enabled.Value) on.Add(st.LegacyNames[i] + "=启用");
                    }
                    if (on.Count > 0) s.Task.Error = Append(s.Task.Error, "v1 闸门仍启用：" + string.Join("、", on.ToArray()));
                }
            }
            catch (Exception ex)
            {
                s.Task.Error = "读取计划任务失败：" + ex.Message;
            }

            try { s.BaselineExists = System.IO.File.Exists(paths.BaselinePath); }
            catch { }
            return s;
        }

        private static QuietModeConfig LoadConfig(QuietModePaths paths, StatusSnapshot s)
        {
            s.ConfigPath = paths.ConfigPath;
            try
            {
                ConfigLoadResult lr = new ConfigStore(paths).Load(false);
                s.ConfigPath = string.IsNullOrEmpty(lr.SourcePath) ? paths.ConfigPath : lr.SourcePath;
                s.ConfigOk = lr.Usable;
                if (!lr.Loaded) s.ConfigError = lr.Error ?? "配置无法读取";
                else if (!lr.Usable) s.ConfigError = lr.Report == null ? "配置校验未通过" : lr.Report.ToText();
                if (lr.UsedBackup) s.ConfigWarnings.Add("已回退到 config.backup.json（last-known-good）");
                if (lr.Report != null) foreach (string w in lr.Report.Warnings) s.ConfigWarnings.Add(w);
                if (lr.Config != null) return lr.Config;
            }
            catch (Exception ex)
            {
                s.ConfigOk = false;
                s.ConfigError = "读取配置异常：" + ex.Message;
            }
            // 配置坏了也必须能显示界面：用默认策略撑住排版，但 ConfigOk=false，
            // 上层据此显示「配置不可用」，绝不把默认值冒充成当前策略。
            return ConfigStore.DefaultPolicy();
        }

        private static FanStatus ReadFan(FanSettings fan)
        {
            FanStatus f = new FanStatus();
            f.Enabled = fan != null && fan.Enabled;
            f.NormalProfile = fan == null ? "" : System.IO.Path.GetFileName(fan.NormalProfile ?? "");
            f.SilentProfile = fan == null ? "" : System.IO.Path.GetFileName(fan.SilentProfile ?? "");
            if (!f.Enabled) { f.Match = "disabled"; return f; }

            try
            {
                FanObservation o = FanProbe.Read(fan, false);
                f.Available = o.Available;
                f.CurrentProfile = o.CurrentProfile ?? "";
                f.Error = o.Error ?? "";
                if (!o.Available) f.Match = "unknown";
                else if (Same(f.CurrentProfile, f.SilentProfile)) f.Match = "silent";
                else if (Same(f.CurrentProfile, f.NormalProfile)) f.Match = "normal";
                else f.Match = "other";
            }
            catch (Exception ex)
            {
                f.Match = "unknown";
                f.Error = ex.Message;
            }
            return f;
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                   && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static List<DiskStatus> ReadDisks(DiskSettings settings)
        {
            List<DiskStatus> list = new List<DiskStatus>();
            if (settings == null) return list;

            List<DiskSnapshot> all;
            try { all = DiskInventory.Query(); }
            catch (Exception ex)
            {
                foreach (DiskTarget t in settings.Targets)
                {
                    if (!t.Enabled) continue;
                    DiskStatus d = new DiskStatus();
                    d.MatchStatus = "probe-failed";
                    d.MatchReason = ex.Message;
                    d.SerialNumber = t.SerialNumber ?? "";
                    d.FriendlyName = t.FriendlyName ?? "";
                    list.Add(d);
                }
                return list;
            }

            foreach (DiskTarget t in settings.Targets)
            {
                if (!t.Enabled) continue;
                DiskMatch m = DiskIdentityResolver.Resolve(t, all);
                DiskStatus d = new DiskStatus();
                d.Matched = m.Unique;
                d.MatchStatus = m.StatusText;
                d.MatchReason = m.Reason ?? "";
                if (m.Disk != null)
                {
                    d.Number = m.Disk.Number;
                    d.UniqueId = m.Disk.UniqueId ?? "";
                    d.SerialNumber = m.Disk.SerialNumber ?? "";
                    d.FriendlyName = m.Disk.FriendlyName ?? "";
                    d.SizeBytes = m.Disk.SizeBytes;
                    d.Offline = m.Disk.IsOffline;
                    try
                    {
                        foreach (DiskVolumeInfo v in DiskProbe.QueryVolumes(m.Disk.Number))
                        {
                            // WMI 在无盘符时返回 "\0"，直接拼会得到 ":" 这种假盘符
                            string l = (v.DriveLetter ?? "").Trim().TrimEnd(':');
                            if (l.Length == 1 && l[0] >= 'A' && l[0] <= 'Z') d.Letters.Add(l + ":");
                        }
                    }
                    catch { }
                }
                else
                {
                    d.SerialNumber = t.SerialNumber ?? "";
                    d.FriendlyName = t.FriendlyName ?? "";
                }
                list.Add(d);
            }
            return list;
        }

        private static SpinDownStatus ReadSpinDown(SpinDownSettings cfg)
        {
            SpinDownStatus s = new SpinDownStatus();
            s.ConfiguredMinutes = cfg == null ? 0 : cfg.Minutes;
            s.ConfiguredEnabled = cfg != null && cfg.Enabled;
            try
            {
                PowerPlanSnapshot p = PowerPlanReader.Capture();
                s.AcSeconds = p.DiskIdleAcSeconds;
                s.DcSeconds = p.DiskIdleDcSeconds;
                s.Error = p.Error ?? "";
            }
            catch (Exception ex)
            {
                s.Error = ex.Message;
            }
            return s;
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static string Append(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return a + "；" + b;
        }
    }
}
