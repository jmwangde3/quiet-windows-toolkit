using System;
using System.Collections.Generic;

namespace QuietMode.Windows.Native
{
    public sealed class TaskQueryResult
    {
        public bool? Exists;
        public bool? Enabled;
        /// <summary>TASK_STATE 名称：Unknown / Disabled / Queued / Ready / Running。</summary>
        public string State = "";
        public string LastRunTime = "";
        public string LastResult = "";
        public string Xml = "";
        public string Error = "";
    }

    /// <summary>
    /// 计划任务只读查询。走 Task Scheduler COM，避免 schtasks 控制台编码把中文任务名/XML 弄坏。
    /// 非提权下部分系统任务会拒绝读取，此时返回 Exists=null + Error，不猜。
    /// </summary>
    public static class TaskSchedulerClient
    {
        private static readonly object _sync = new object();

        public static TaskQueryResult Query(string folderPath, string name)
        {
            TaskQueryResult r = new TaskQueryResult();
            lock (_sync)
            {
                object service = null;
                try
                {
                    Type t = Type.GetTypeFromProgID("Schedule.Service");
                    if (t == null)
                    {
                        r.Error = "无法创建 Schedule.Service COM 对象";
                        return r;
                    }

                    service = Activator.CreateInstance(t);
                    dynamic svc = service;
                    svc.Connect();

                    string path = NormalizeFolder(folderPath);
                    dynamic folder;
                    try
                    {
                        folder = string.IsNullOrEmpty(path) ? svc.GetFolder("\\") : svc.GetFolder(path);
                    }
                    catch (Exception ex)
                    {
                        // 0x80070002 = 任务/文件夹不存在；其它按错误上报
                        if (IsNotFound(ex)) { r.Exists = false; return r; }
                        r.Error = "打开任务文件夹失败：" + Short(ex);
                        return r;
                    }

                    dynamic task;
                    try
                    {
                        task = folder.GetTask(name);
                    }
                    catch (Exception ex)
                    {
                        // 0x80070002 = 任务不存在；其它按错误上报
                        if (IsNotFound(ex)) { r.Exists = false; return r; }
                        r.Error = "读取任务失败：" + Short(ex);
                        return r;
                    }

                    r.Exists = true;
                    try { r.Enabled = (bool)task.Enabled; }
                    catch (Exception ex) { r.Error = "读取 Enabled 失败：" + Short(ex); }
                    try { r.State = StateName((int)task.State); }
                    catch (Exception ex) { r.Error = Append(r.Error, "读取 State 失败：" + Short(ex)); }
                    try
                    {
                        DateTime last = (DateTime)task.LastRunTime;
                        r.LastRunTime = last.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch { }
                    try { r.LastResult = ((int)task.LastTaskResult).ToString(System.Globalization.CultureInfo.InvariantCulture); }
                    catch { }
                    try { r.Xml = (string)task.Xml; }
                    catch (Exception ex) { r.Error = Append(r.Error, "读取 XML 失败：" + Short(ex)); }
                    return r;
                }
                catch (Exception ex)
                {
                    r.Error = "Task Scheduler 不可用：" + Short(ex);
                    return r;
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
        }

        /// <summary>枚举任务文件夹下的任务名（只读）。</summary>
        public static List<string> ListTaskNames(string folderPath, out string error)
        {
            List<string> names = new List<string>();
            error = "";
            object service = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Schedule.Service");
                if (t == null) { error = "无法创建 Schedule.Service COM 对象"; return names; }
                service = Activator.CreateInstance(t);
                dynamic svc = service;
                svc.Connect();
                string path = NormalizeFolder(folderPath);
                dynamic folder = string.IsNullOrEmpty(path) ? svc.GetFolder("\\") : svc.GetFolder(path);
                object tasksObj = folder.GetTasks(1);   // 1 = 只取可见任务
                System.Collections.IEnumerable enumerable = tasksObj as System.Collections.IEnumerable;
                if (enumerable != null)
                {
                    foreach (object o in enumerable)
                    {
                        try { names.Add((string)((dynamic)o).Name); }
                        catch { }
                    }
                }
                else
                {
                    dynamic tasks = tasksObj;
                    int count = (int)tasks.Count;
                    for (int i = 1; i <= count; i++)
                    {
                        try { names.Add((string)tasks.Item(i).Name); }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                error = Short(ex);
            }
            finally
            {
                if (service != null) { try { System.Runtime.InteropServices.Marshal.ReleaseComObject(service); } catch { } }
            }
            return names;
        }

        /// <summary>TASK_STATE 枚举转名字。直接 ToString 在部分系统上会得到数字。</summary>
        public static string StateName(int state)
        {
            switch (state)
            {
                case 0: return "Unknown";
                case 1: return "Disabled";
                case 2: return "Queued";
                case 3: return "Ready";
                case 4: return "Running";
                default: return "State" + state.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        private static string NormalizeFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return "";
            return folderPath.Trim().Trim('\\');
        }

        private static bool IsNotFound(Exception ex)
        {
            Exception cur = ex;
            while (cur != null)
            {
                System.Runtime.InteropServices.COMException com = cur as System.Runtime.InteropServices.COMException;
                if (com != null && (uint)com.HResult == 0x80070002u) return true;
                if (cur is System.IO.FileNotFoundException) return true;
                string msg = cur.Message ?? "";
                if (msg.IndexOf("0x80070002", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                cur = cur.InnerException;
            }
            return false;
        }

        private static string Short(Exception ex)
        {
            string msg = ex.Message == null ? ex.GetType().Name : ex.Message.Replace("\r", " ").Replace("\n", " ").Trim();
            System.Runtime.InteropServices.COMException com = ex as System.Runtime.InteropServices.COMException;
            if (com != null)
            {
                if ((uint)com.HResult == 0x80070005u) return "0x80070005 拒绝访问（读取该任务需要管理员权限）";
                return "0x" + ((uint)com.HResult).ToString("X8") + " " + msg;
            }
            return msg;
        }

        private static string Append(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? b : a + "；" + b;
        }
    }
}
