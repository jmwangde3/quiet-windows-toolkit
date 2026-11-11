using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Models;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    public sealed class SchedulerStatus
    {
        public string TaskPath = SchedulerInstaller.FolderPath;
        public string TaskName = SchedulerInstaller.TaskName;
        public bool? Exists;
        public string State = "";
        public string LastRunTime = "";
        public string LastResult = "";
        public string Error = "";
        /// <summary>v1 遗留闸门任务，必须全部停用才算「只有一个权威控制器」。</summary>
        public List<TaskQueryResult> Legacy = new List<TaskQueryResult>();
        public List<string> LegacyNames = new List<string>();

        /// <summary>v1 遗留闸门里读不到状态的条目数（多半是 \QuietHDD\ 的 ACL 需要提权）。</summary>
        public bool LegacyUnknown
        {
            get
            {
                foreach (TaskQueryResult t in Legacy)
                {
                    if (!t.Exists.HasValue) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 是否可能存在重复调度。读不到就按「可能有」处理 —— 不能把「看不见」当成「不存在」，
        /// 这正是 v1 那次「10:02 没联机」的根因之一。
        /// </summary>
        public bool Conflict
        {
            get
            {
                foreach (TaskQueryResult t in Legacy)
                {
                    if (!t.Exists.HasValue) return true;
                    if (t.Exists.Value && t.Enabled != false) return true;
                }
                return false;
            }
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("v2 任务：" + TaskPath + TaskName);
            sb.AppendLine("  存在      : " + (Exists.HasValue ? (Exists.Value ? "是" : "否") : "无法确定"));
            if (Exists == true)
            {
                sb.AppendLine("  状态      : " + State);
                sb.AppendLine("  上次运行  : " + LastRunTime + "（结果 " + LastResult + "）");
            }
            if (!string.IsNullOrEmpty(Error)) sb.AppendLine("  错误      : " + Error);
            sb.AppendLine("v1 遗留闸门：");
            if (LegacyNames.Count == 0) sb.AppendLine("  （未检查）");
            for (int i = 0; i < LegacyNames.Count; i++)
            {
                TaskQueryResult t = Legacy[i];
                string s = !t.Exists.HasValue ? "无法确定" : (t.Exists.Value ? "存在" : "不存在");
                string e = t.Enabled.HasValue ? (t.Enabled.Value ? "启用" : "已禁用") : "?";
                sb.AppendLine("  " + LegacyNames[i] + " : " + s + " / " + e
                              + (string.IsNullOrEmpty(t.Error) ? "" : "（" + t.Error + "）"));
            }
            sb.AppendLine("结论：" + (Conflict
                ? (LegacyUnknown
                    ? "无法确认 v1 闸门是否仍在启用（需要管理员权限读取 \\QuietHDD\\）；按「可能有重复调度」处理"
                    : "存在重复调度（v1 闸门仍在启用）")
                : "没有检测到重复调度"));
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// 唯一计划任务的注册/卸载/检查（规格 §6.1：<c>\QuietMode\QuietMode-Reconcile</c>）。
    ///
    /// 触发器：每天 23:58、每天 10:02、开机后 2 分钟、睡眠恢复、解锁、夜间每 N 分钟校验。
    /// 设置：SYSTEM + Highest、WakeToRun=false、忽略并发、执行上限 10 分钟。
    /// 夜间重复校验用 23:58 那个触发器的 Repetition 实现（PT{N}M / 持续 10 小时），
    /// 不用第二个任务，避免又出现两套调度。
    /// </summary>
    public static class SchedulerInstaller
    {
        public const string FolderPath = @"\QuietMode\";
        public const string TaskName = "QuietMode-Reconcile";
        public const string FullName = FolderPath + TaskName;

        public static readonly string[] LegacyTaskPaths =
        {
            @"\QuietHDD\QuietHDD_NightGate",
            @"\QuietHDD\QuietHDD_DiskGate"
        };

        /// <summary>生成任务 XML。纯函数，方便单测和人工核对。</summary>
        public static string BuildXml(ScheduleSettings s, string command, string arguments, string workingDir, string description)
        {
            if (s == null) s = new ScheduleSettings();
            int interval = s.ReconcileIntervalMinutes > 0 ? s.ReconcileIntervalMinutes : 10;
            string night = string.IsNullOrWhiteSpace(s.NightStart) ? "23:58" : s.NightStart.Trim();
            string day = string.IsNullOrWhiteSpace(s.DayStart) ? "10:02" : s.DayStart.Trim();
            if (TimeText.ToMinutes(night) < 0) night = "23:58";
            if (TimeText.ToMinutes(day) < 0) day = "10:02";

            string start = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            sb.AppendLine("  <RegistrationInfo>");
            sb.AppendLine("    <Author>QuietMode</Author>");
            sb.AppendLine("    <Description>" + Escape(description ?? "QuietMode 统一调度：维护闸门 + 磁盘脱机 + 风扇配置") + "</Description>");
            sb.AppendLine("    <URI>" + FullName + "</URI>");
            sb.AppendLine("  </RegistrationInfo>");
            sb.AppendLine("  <Triggers>");
            sb.AppendLine(CalendarTrigger(start + "T" + night + ":00", "PT" + interval + "M", "PT10H", true));
            sb.AppendLine(CalendarTrigger(start + "T" + day + ":00", "", "", false));
            sb.AppendLine("    <BootTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Delay>PT2M</Delay>");
            sb.AppendLine("    </BootTrigger>");
            sb.AppendLine("    <EventTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"System\"&gt;&lt;Select Path=\"System\"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>");
            sb.AppendLine("      <Delay>PT30S</Delay>");
            sb.AppendLine("    </EventTrigger>");
            sb.AppendLine("    <EventTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Subscription>&lt;QueryList&gt;&lt;Query Id=\"0\" Path=\"Security\"&gt;&lt;Select Path=\"Security\"&gt;*[System[Provider[@Name='Microsoft-Windows-Security-Auditing'] and EventID=4801]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>");
            sb.AppendLine("      <Delay>PT10S</Delay>");
            sb.AppendLine("    </EventTrigger>");
            sb.AppendLine("    <LogonTrigger>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <Delay>PT45S</Delay>");
            sb.AppendLine("    </LogonTrigger>");
            sb.AppendLine("  </Triggers>");
            sb.AppendLine("  <Principals>");
            sb.AppendLine("    <Principal id=\"Author\">");
            sb.AppendLine("      <UserId>S-1-5-18</UserId>");
            sb.AppendLine("      <RunLevel>HighestAvailable</RunLevel>");
            sb.AppendLine("    </Principal>");
            sb.AppendLine("  </Principals>");
            sb.AppendLine("  <Settings>");
            sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>");
            sb.AppendLine("    <StartWhenAvailable>true</StartWhenAvailable>");
            sb.AppendLine("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>");
            sb.AppendLine("    <IdleSettings>");
            sb.AppendLine("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            sb.AppendLine("      <RestartOnIdle>false</RestartOnIdle>");
            sb.AppendLine("    </IdleSettings>");
            sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>");
            sb.AppendLine("    <Enabled>true</Enabled>");
            sb.AppendLine("    <Hidden>false</Hidden>");
            sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            sb.AppendLine("    <WakeToRun>false</WakeToRun>");
            sb.AppendLine("    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>");
            sb.AppendLine("    <Priority>7</Priority>");
            sb.AppendLine("  </Settings>");
            sb.AppendLine("  <Actions Context=\"Author\">");
            sb.AppendLine("    <Exec>");
            sb.AppendLine("      <Command>" + Escape(command) + "</Command>");
            sb.AppendLine("      <Arguments>" + Escape(arguments) + "</Arguments>");
            if (!string.IsNullOrEmpty(workingDir))
                sb.AppendLine("      <WorkingDirectory>" + Escape(workingDir) + "</WorkingDirectory>");
            sb.AppendLine("    </Exec>");
            sb.AppendLine("  </Actions>");
            sb.AppendLine("</Task>");
            return sb.ToString();
        }

        private static string CalendarTrigger(string startBoundary, string interval, string duration, bool withRepetition)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("    <CalendarTrigger>");
            sb.AppendLine("      <StartBoundary>" + startBoundary + "</StartBoundary>");
            sb.AppendLine("      <Enabled>true</Enabled>");
            sb.AppendLine("      <ScheduleByDay>");
            sb.AppendLine("        <DaysInterval>1</DaysInterval>");
            sb.AppendLine("      </ScheduleByDay>");
            if (withRepetition && interval.Length > 0)
            {
                sb.AppendLine("      <Repetition>");
                sb.AppendLine("        <Interval>" + interval + "</Interval>");
                sb.AppendLine("        <Duration>" + duration + "</Duration>");
                sb.AppendLine("        <StopAtDurationEnd>true</StopAtDurationEnd>");
                sb.AppendLine("      </Repetition>");
            }
            sb.Append("    </CalendarTrigger>");
            return sb.ToString();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        /// <summary>默认命令行：qmctl reconcile --apply --trigger schedule --root &lt;root&gt; --quiet。</summary>
        public static string BuildCommandLine(string root, out string command, out string arguments, out string workingDir)
        {
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qmctl.exe");
            command = exe;
            arguments = "reconcile --apply --trigger schedule"
                        + (string.IsNullOrEmpty(root) ? "" : " --root \"" + root + "\"")
                        + " --quiet";
            workingDir = AppDomain.CurrentDomain.BaseDirectory;
            return exe;
        }

        public static SchedulerStatus Inspect()
        {
            SchedulerStatus st = new SchedulerStatus();
            TaskQueryResult t = TaskSchedulerClient.Query(FolderPath, TaskName);
            st.Exists = t.Exists;
            st.State = t.State;
            st.LastRunTime = t.LastRunTime;
            st.LastResult = t.LastResult;
            st.Error = t.Error;

            foreach (string full in LegacyTaskPaths)
            {
                string folder = full.Substring(0, full.LastIndexOf('\\') + 1);
                string name = full.Substring(full.LastIndexOf('\\') + 1);
                st.LegacyNames.Add(full);
                st.Legacy.Add(TaskSchedulerClient.Query(folder, name));
            }
            return st;
        }

        /// <summary>注册（或覆盖）唯一任务。需要管理员。</summary>
        public static string Install(ScheduleSettings s, string root, bool apply, out string xml)
        {
            string command, arguments, workingDir;
            BuildCommandLine(root, out command, out arguments, out workingDir);
            xml = BuildXml(s, command, arguments, workingDir, "QuietMode 统一调度");
            if (!apply) return "";

            object service = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return "无法创建 Schedule.Service COM 对象";
                service = Activator.CreateInstance(type);
                dynamic svc = service;
                svc.Connect();

                dynamic rootFolder = svc.GetFolder("\\");
                dynamic folder;
                try { folder = rootFolder.GetFolder("QuietMode"); }
                catch { folder = rootFolder.CreateFolder("QuietMode"); }

                const int TASK_CREATE_OR_UPDATE = 6;
                const int TASK_LOGON_SERVICE_ACCOUNT = 5;
                folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, "SYSTEM", null, TASK_LOGON_SERVICE_ACCOUNT, null);
                return "";
            }
            catch (Exception ex)
            {
                return "注册任务失败：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                if (service != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(service); }
                    catch { }
                }
            }
        }

        public static string Uninstall(bool apply)
        {
            if (!apply) return "";
            object service = null;
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return "无法创建 Schedule.Service COM 对象";
                service = Activator.CreateInstance(type);
                dynamic svc = service;
                svc.Connect();
                dynamic folder = svc.GetFolder("QuietMode");
                folder.DeleteTask(TaskName, 0);
                return "";
            }
            catch (Exception ex)
            {
                return "删除任务失败：" + ex.Message;
            }
            finally
            {
                if (service != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(service); }
                    catch { }
                }
            }
        }

        /// <summary>停用 v1 遗留闸门。这是阶段 4 里唯一会改系统状态的操作之一，默认演练。</summary>
        public static List<string> DisableLegacy(bool apply)
        {
            List<string> log = new List<string>();
            foreach (string full in LegacyTaskPaths)
            {
                string folderPath = full.Substring(0, full.LastIndexOf('\\') + 1);
                string name = full.Substring(full.LastIndexOf('\\') + 1);
                TaskQueryResult q = TaskSchedulerClient.Query(folderPath, name);
                if (q.Exists == false) { log.Add(full + "：不存在，跳过"); continue; }
                if (q.Exists == null) { log.Add(full + "：无法确定是否存在（" + q.Error + "）"); continue; }
                if (q.Enabled == false) { log.Add(full + "：已禁用"); continue; }
                if (!apply) { log.Add(full + "：需要禁用（演练）"); continue; }

                try
                {
                    object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
                    dynamic svc = service;
                    svc.Connect();
                    dynamic folder = svc.GetFolder(folderPath.TrimEnd('\\'));
                    dynamic task = folder.GetTask(name);
                    task.Enabled = false;
                    TaskQueryResult after = TaskSchedulerClient.Query(folderPath, name);
                    log.Add(full + "：已禁用，回读 Enabled=" + (after.Enabled.HasValue ? after.Enabled.Value.ToString() : "?"));
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(service);
                }
                catch (Exception ex)
                {
                    log.Add(full + "：禁用失败（" + ex.Message + "）");
                }
            }
            return log;
        }
    }
}
