using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Models;
using QuietMode.Core.Scheduling;
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
                    // 「存在但读不到是否启用」同样是未知：ACL 挡住的时候 Enabled 为 null，
                    // 早先这条被当成「仍在启用」，于是每次查状态都误报重复调度。
                    if (t.Exists.Value && !t.Enabled.HasValue) return true;
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

        /// <summary>
        /// v1 留下的全部历史闸门。v1 前后装过两个位置（带文件夹和不带文件夹），
        /// 少认一个就会出现「两套调度同时控制设备」—— 而这正是 v1 出问题的形态。
        /// 这里只按「任务全名」查找，不依赖磁盘号、不依赖用户名。
        /// </summary>
        public static readonly string[] LegacyTaskPaths =
        {
            @"\QuietHDD\QuietHDD_NightGate",
            @"\QuietHDD\QuietHDD_DiskGate",
            @"\QuietHDD_NightGate",
            @"\QuietHDD_DiskGate"
        };

        /// <summary>夜间窗口的分钟数（nightStart → dayStart，跨午夜自动 +1 天）。非法返回 0。</summary>
        public static int WindowMinutes(string nightStart, string dayStart)
        {
            return TaskXml.WindowMinutes(nightStart, dayStart);
        }

        /// <summary>ISO-8601 时长；实现在 TaskXml，两边只有一份。</summary>
        public static string IsoDuration(int minutes)
        {
            return TaskXml.IsoDuration(minutes);
        }

        /// <summary>生成任务 XML。纯函数，方便单测和人工核对；实现见 Core 的 TaskXml。</summary>
        public static string BuildXml(ScheduleSettings s, string command, string arguments, string workingDir, string description)
        {
            return TaskXml.Build(s, command, arguments, workingDir, description, FullName, DateTime.Now);
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

        /// <summary>
        /// 任务对象的安全描述符：Administrators / SYSTEM 全控，普通用户只读。
        /// Task Scheduler 在注册时把这份 SDDL 固化进
        /// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree\&lt;路径&gt;\SD，
        /// 访问检查用的是它；事后改任务文件（C:\Windows\System32\Tasks\...）的 ACL
        /// 改不到这份副本。所以「普通用户能看任务状态」只能在注册这一步给。
        /// </summary>
        public const string TaskSddl = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;BU)";

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
                // sddl 不能传 null：默认只给 BA/SY，未提权的托盘会拿到 0x80070005。
                folder.RegisterTask(TaskName, xml, TASK_CREATE_OR_UPDATE, "SYSTEM", null, TASK_LOGON_SERVICE_ACCOUNT, TaskSddl);
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
