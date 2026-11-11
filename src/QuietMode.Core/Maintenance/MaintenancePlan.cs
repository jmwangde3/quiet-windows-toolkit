using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Maintenance
{
    public enum MaintenanceAction
    {
        Unknown = 0,
        StopTask,
        DisableTask,
        EnableTask,
        StopService,
        StartService,
        SetServiceStart,
        SetRegistryValue,
        DeleteRegistryValue
    }

    public enum StepStatus
    {
        Pending = 0,
        Done,
        Failed,
        Skipped,
        NotPresent,
        /// <summary>脚本根本没跑（例如未提权），既不是成功也不是失败。</summary>
        NotRun
    }

    /// <summary>
    /// 一个可执行步骤。凡是「改变系统状态」的动作都必须在计划里显式出现，
    /// 执行层不允许自己发挥（规格 §4.2 的顺序就是这样被固化成列表的）。
    /// </summary>
    public sealed class MaintenanceStep
    {
        public int Order;
        public MaintenanceAction Action = MaintenanceAction.Unknown;
        public string Target = "";          // \Path\Name 或服务名或 注册表键\值名
        public string Path = "";
        public string Name = "";
        public string DesiredValue = "";    // 注册表值 / Start 数值
        public string Kind = "DWord";
        public string Reason = "";
        /// <summary>关键步骤失败会导致整体降级；非关键步骤失败只记录。</summary>
        public bool Critical = true;

        public StepStatus Status = StepStatus.Pending;
        public string Error = "";
        public string ObservedAfter = "";

        public string ActionText
        {
            get
            {
                switch (Action)
                {
                    case MaintenanceAction.StopTask: return "停止任务";
                    case MaintenanceAction.DisableTask: return "禁用任务";
                    case MaintenanceAction.EnableTask: return "启用任务";
                    case MaintenanceAction.StopService: return "停止服务";
                    case MaintenanceAction.StartService: return "启动服务";
                    case MaintenanceAction.SetServiceStart: return "设置服务启动类型";
                    case MaintenanceAction.SetRegistryValue: return "写注册表值";
                    case MaintenanceAction.DeleteRegistryValue: return "删除注册表值";
                    default: return "未知动作";
                }
            }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("order", Order);
            o.Set("action", Action.ToString());
            o.Set("target", Target ?? "");
            o.Set("path", Path ?? "");
            o.Set("name", Name ?? "");
            o.Set("desiredValue", DesiredValue ?? "");
            o.Set("kind", Kind ?? "");
            o.Set("reason", Reason ?? "");
            o.Set("critical", Critical);
            o.Set("status", Status.ToString());
            o.Set("error", Error ?? "");
            o.Set("observedAfter", ObservedAfter ?? "");
            return o;
        }

        public static MaintenanceStep FromJson(JsonObject o)
        {
            MaintenanceStep s = new MaintenanceStep();
            s.Order = o.GetInt("order", 0);
            s.Action = ParseAction(o.GetString("action", "Unknown"));
            s.Target = o.GetString("target", "");
            s.Path = o.GetString("path", "");
            s.Name = o.GetString("name", "");
            s.DesiredValue = o.GetString("desiredValue", "");
            s.Kind = o.GetString("kind", "DWord");
            s.Reason = o.GetString("reason", "");
            s.Critical = o.GetBool("critical", true);
            s.Status = ParseStatus(o.GetString("status", "Pending"));
            s.Error = o.GetString("error", "");
            s.ObservedAfter = o.GetString("observedAfter", "");
            return s;
        }

        private static MaintenanceAction ParseAction(string s)
        {
            foreach (MaintenanceAction a in Enum.GetValues(typeof(MaintenanceAction)))
            {
                if (string.Equals(a.ToString(), s, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return MaintenanceAction.Unknown;
        }

        private static StepStatus ParseStatus(string s)
        {
            foreach (StepStatus v in Enum.GetValues(typeof(StepStatus)))
            {
                if (string.Equals(v.ToString(), s, StringComparison.OrdinalIgnoreCase)) return v;
            }
            return StepStatus.Pending;
        }

        public string ToLine()
        {
            string head = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0,2}. {1,-14} {2}", Order, ActionText, Target);
            if (!string.IsNullOrEmpty(DesiredValue)) head += " = " + DesiredValue;
            switch (Status)
            {
                case StepStatus.Done: head += "  [完成]"; break;
                case StepStatus.Failed: head += "  [失败] " + Error; break;
                case StepStatus.Skipped: head += "  [跳过] " + Error; break;
                case StepStatus.NotPresent: head += "  [不存在]"; break;
                case StepStatus.NotRun: head += "  [未执行] " + Error; break;
                default: head += "  [待执行]"; break;
            }
            return head;
        }
    }

    public sealed class MaintenancePlan
    {
        /// <summary>night | day | verify</summary>
        public string Phase = "";
        public List<MaintenanceStep> Steps = new List<MaintenanceStep>();
        public List<string> Notes = new List<string>();

        public MaintenanceStep Add(MaintenanceAction action, string target, string reason)
        {
            MaintenanceStep s = new MaintenanceStep();
            s.Order = Steps.Count + 1;
            s.Action = action;
            s.Target = target ?? "";
            s.Reason = reason ?? "";
            Steps.Add(s);
            return s;
        }

        public MaintenanceStep AddTask(MaintenanceAction action, string path, string name, string reason)
        {
            MaintenanceStep s = Add(action, (path ?? "").TrimEnd('\\') + "\\" + name, reason);
            s.Path = path ?? "";
            s.Name = name ?? "";
            return s;
        }

        public MaintenanceStep AddRegistry(MaintenanceAction action, string keyPath, string valueName,
                                           string desiredValue, string kind, string reason)
        {
            MaintenanceStep s = Add(action, keyPath + "\\" + valueName, reason);
            s.Path = keyPath;
            s.Name = valueName;
            s.DesiredValue = desiredValue ?? "";
            s.Kind = string.IsNullOrEmpty(kind) ? "DWord" : kind;
            return s;
        }

        public MaintenanceStep AddService(MaintenanceAction action, string serviceName, string desiredValue, string reason)
        {
            MaintenanceStep s = Add(action, serviceName, reason);
            s.Name = serviceName;
            s.DesiredValue = desiredValue ?? "";
            return s;
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("phase", Phase ?? "");
            JsonArray arr;
            o.SetArray("steps", out arr);
            foreach (MaintenanceStep s in Steps) arr.Add(s.ToJson());
            JsonArray notes;
            o.SetArray("notes", out notes);
            foreach (string n in Notes) notes.Add(n);
            return o;
        }
    }
}
