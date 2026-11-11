using System;
using System.IO;
using QuietMode.Core.Json;
using QuietMode.Core.Models;
using QuietMode.Core.Scheduling;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// Phase 5 单测：托盘契约层。托盘不引用 Core，只认 qmctl 的 JSON，
    /// 所以这里的重点是「契约字段稳定」和「未知不冒充已知」。
    /// </summary>
    public static partial class Program
    {
        private static void RunTrayContractTests()
        {
            Run("SystemClock：运行时长合理且不受 32 位回绕影响", delegate
            {
                TimeSpan? up = SystemClock.Uptime();
                True(up.HasValue, "Uptime 不应为 null");
                True(up.Value.TotalSeconds > 0, "Uptime 应大于 0，实际 " + up.Value);
                True(up.Value.TotalDays < 3650, "Uptime 不可能超过 10 年，实际 " + up.Value);
            });

            Run("StateMachine.IsLastRunFailed：NotRun 不算失败", delegate
            {
                Eq(false, StateMachine.IsLastRunFailed(null));
                Eq(false, StateMachine.IsLastRunFailed(new QuietModeState()));
                Eq(false, StateMachine.IsLastRunFailed(new QuietModeState { LastVerdict = "Ok" }));
                Eq(false, StateMachine.IsLastRunFailed(new QuietModeState { LastVerdict = "NotRun" }));
                Eq(true, StateMachine.IsLastRunFailed(new QuietModeState { LastVerdict = "Failed" }));
                Eq(true, StateMachine.IsLastRunFailed(new QuietModeState { LastVerdict = "Degraded" }));
            });

            Run("状态快照：未知磁盘状态序列化为 null 而不是 false", delegate
            {
                StatusSnapshot s = new StatusSnapshot();
                s.ConfigExists = true;
                DiskStatus d = new DiskStatus();
                d.Matched = false;
                d.MatchStatus = "not-found";
                s.Disks.Add(d);

                JsonObject o = s.ToJson();
                JsonObject disks = o.GetObject("disks");
                True(disks != null, "缺少 disks 节点");
                JsonArray arr = disks.GetArray("targets");
                Eq(1, arr.Count);
                JsonNode offline = arr[0].AsObject().Get("offline");
                True(offline == null || offline.IsNull, "未匹配磁盘的 offline 必须是 null（未知），不是 false");
                Eq(false, disks.GetBool("anyOffline", true));
                Eq(1, disks.GetInt("unknown", 0));
            });

            Run("状态快照：关键字段齐全（托盘按名取值）", delegate
            {
                StatusSnapshot s = new StatusSnapshot();
                s.ConfigOk = true;
                s.ConfigExists = false;
                s.Window = ScheduleWindow.Evaluate(new ScheduleSettings(), DateTime.Now, TimeSpan.FromHours(1));
                DecisionInput input = new DecisionInput();
                input.Schedule = s.Schedule;
                input.State = s.State;
                input.Window = s.Window;
                s.Decision = StateMachine.Decide(input);

                JsonObject o = s.ToJson();
                string[] top = new string[] { "ok", "generatedUtc", "elevated", "config", "schedule", "startup",
                                              "window", "controller", "state", "fan", "disks", "spinDown", "task", "baselineExists" };
                foreach (string k in top) True(o.Get(k) != null, "缺少顶层字段 " + k);

                Eq(false, o.GetObject("config").GetBool("exists", true));
                True(o.GetObject("controller").GetString("state", "") != "", "controller.state 不能为空");
                True(o.GetObject("window").Get("isNight") != null, "window.isNight 缺失");
                True(o.GetObject("task").Get("exists") != null, "task.exists 缺失（可为 null）");
                True(o.GetObject("spinDown").Get("acSeconds") != null, "spinDown.acSeconds 缺失");
            });

            Run("状态快照：磁盘回环不影响 JSON 结构", delegate
            {
                StatusSnapshot s = new StatusSnapshot();
                DiskStatus d = new DiskStatus();
                d.Matched = true;
                d.Number = 1;
                d.Offline = true;
                d.Letters.Add("D:");
                s.Disks.Add(d);
                JsonObject o = s.ToJson();
                JsonArray arr = o.GetObject("disks").GetArray("targets");
                Eq(true, arr[0].AsObject().GetBool("offline", false));
                Eq("D:", arr[0].AsObject().GetArray("letters")[0].AsString());
                Eq(true, o.GetObject("disks").GetBool("anyOffline", false));
            });
        }
    }
}
