using System;
using System.Collections.Generic;
using QuietMode.Core.Json;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// Phase 3 单测：维护计划器与验证器。全部合成数据，不接触真实系统。
    /// 重点不是「能跑」，而是「不存在的任务不报错、停不下来的任务会降级、恢复只认 baseline」。
    /// </summary>
    public static partial class Program
    {
        internal static void RunMaintenanceTests()
        {
            Run("维护清单：自检不落在禁改范围", TestCatalogSelfCheck);
            Run("维护清单：Windows Update 被识别为禁改范围", TestCatalogForbidden);
            Run("夜间计划：不存在的任务跳过且不计失败", TestNightPlanNotPresent);
            Run("夜间计划：正在运行的任务先停止再禁用", TestNightPlanStopsRunningTasks);
            Run("夜间计划：关闭维护总开关后不产生任何步骤", TestNightPlanDisabled);
            Run("夜间验证：任务仍启用 → Failed", TestNightVerifyEnabledFails);
            Run("夜间验证：任务已禁用但仍在运行 → Degraded", TestNightVerifyRunningDegrades);
            Run("夜间验证：全部达成 → Ok", TestNightVerifyAllGood);
            Run("白天计划：原本启用的任务恢复启用", TestDayPlanEnables);
            Run("白天计划：原本禁用的任务不擅自启用", TestDayPlanKeepsDisabled);
            Run("白天计划：被外部启用时按 baseline 收回禁用", TestDayPlanRestoresDisabled);
            Run("白天计划：baseline 没有的值 → 删除而不是写默认值", TestDayPlanDeletesAbsentValue);
            Run("白天计划：SysMain 按 baseline 的 Start 值恢复", TestDayPlanRestoresSysMain);
            Run("白天验证：注册表被改动 → Failed", TestDayVerifyRegistryDrift);
            Run("白天验证：baseline 无该值但现在有 → Failed", TestDayVerifyUnexpectedValue);
            Run("白天验证：任务消失 → Degraded 而不是 Failed", TestDayVerifyMissingTaskDegrades);
            Run("步骤状态：NotRun 与 Pending 语义不同", TestStepStatusNotRun);
        }

        // ---------------- 构造辅助 ----------------

        private static QuietModeConfig DefaultConfig()
        {
            return new QuietModeConfig();
        }

        private static SystemObservation EmptyObservation()
        {
            SystemObservation obs = new SystemObservation();
            return obs;
        }

        /// <summary>把一个任务写成指定状态；已存在同名条目时覆盖，避免出现重复观测。</summary>
        private static void SetTask(SystemObservation obs, MaintenanceTaskDefinition def,
                                    TaskPresence presence, bool? enabled, string state)
        {
            ObservedTask existing = obs.FindTask(def.Path, def.Name);
            if (existing != null) obs.Tasks.Remove(existing);

            ObservedTask t = new ObservedTask();
            t.Path = def.Path;
            t.Name = def.Name;
            t.Presence = presence;
            t.Enabled = enabled;
            t.State = state ?? "";
            obs.Tasks.Add(t);
        }

        /// <summary>把夜间全部目标任务写成「存在 + 指定启用状态」。</summary>
        private static void SetAllNightTargets(SystemObservation obs, bool enabled, string state)
        {
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                SetTask(obs, def, TaskPresence.Present, enabled, state);
            }
        }

        private static void AddRegistry(SystemObservation obs, string valueName, bool exists, string value)
        {
            ObservedRegistryValue r = new ObservedRegistryValue();
            r.KeyPath = MaintenanceCatalog.MaintenanceRegistryKey;
            r.ValueName = valueName;
            r.Exists = exists;
            r.Kind = "DWord";
            r.Value = value;
            obs.Registry.Add(r);
        }

        private static void SetSysMain(SystemObservation obs, bool exists, int start, string status)
        {
            ObservedService s = new ObservedService();
            s.ServiceName = MaintenanceCatalog.SysMainService;
            s.Exists = exists;
            s.StartValue = start;
            s.Status = status ?? "";
            obs.Services.Add(s);
        }

        private static void GoodNightRegistry(SystemObservation obs)
        {
            AddRegistry(obs, MaintenanceCatalog.MaintenanceBoundaryValue, true, "10");
            AddRegistry(obs, MaintenanceCatalog.MaintenanceWakeValue, true, "0");
        }

        private static BaselineSnapshot BaselineAllEnabled()
        {
            BaselineSnapshot b = new BaselineSnapshot();
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                TaskSnapshot t = new TaskSnapshot();
                t.Path = def.Path;
                t.Name = def.Name;
                t.Exists = true;
                t.Enabled = true;
                b.ScheduledTasks.Add(t);
            }
            return b;
        }

        private static int CountSteps(MaintenancePlan plan, MaintenanceAction action)
        {
            int n = 0;
            foreach (MaintenanceStep s in plan.Steps) if (s.Action == action) n++;
            return n;
        }

        private static MaintenanceStep FirstStep(MaintenancePlan plan, MaintenanceAction action, string taskName)
        {
            foreach (MaintenanceStep s in plan.Steps)
            {
                if (s.Action == action && string.Equals(s.Name, taskName, StringComparison.OrdinalIgnoreCase)) return s;
            }
            return null;
        }

        private static MaintenanceTaskDefinition Def(string name)
        {
            foreach (MaintenanceTaskDefinition d in MaintenanceCatalog.All)
            {
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d;
            }
            throw new Exception("清单里没有任务 " + name);
        }

        // ---------------- 清单 ----------------

        private static void TestCatalogSelfCheck()
        {
            List<string> problems = MaintenanceCatalog.SelfCheck();
            Eq(0, problems.Count);
        }

        private static void TestCatalogForbidden()
        {
            True(MaintenanceCatalog.ForbiddenPrefixOf(@"\Microsoft\Windows\WindowsUpdate\Scheduled Start").Length > 0,
                "Windows Update 应被识别为禁改范围");
            True(MaintenanceCatalog.ForbiddenPrefixOf(@"\Microsoft\Windows\Defrag\ScheduledDefrag") == "",
                "Defrag 不应被禁用范围命中");
        }

        // ---------------- 夜间计划 ----------------

        private static void TestNightPlanNotPresent()
        {
            SystemObservation obs = EmptyObservation();
            foreach (MaintenanceTaskDefinition def in MaintenanceCatalog.NightDisableTargets)
            {
                SetTask(obs, def, TaskPresence.NotPresent, null, "");
            }
            SetSysMain(obs, true, 2, "Running");

            MaintenancePlan plan = MaintenancePlanner.BuildNight(DefaultConfig(), new BaselineSnapshot(), obs);
            Eq(0, CountSteps(plan, MaintenanceAction.DisableTask));
            Eq(0, CountSteps(plan, MaintenanceAction.StopTask));
            True(plan.Notes.Exists(n => n.Contains("不存在")), "应有 NotPresent 说明");
        }

        private static void TestNightPlanStopsRunningTasks()
        {
            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, true, "Ready");
            SetTask(obs, Def("ScheduledDefrag"), TaskPresence.Present, true, "Running");
            SetSysMain(obs, true, 2, "Running");

            MaintenancePlan plan = MaintenancePlanner.BuildNight(DefaultConfig(), new BaselineSnapshot(), obs);

            MaintenanceStep stop = FirstStep(plan, MaintenanceAction.StopTask, "ScheduledDefrag");
            MaintenanceStep disable = FirstStep(plan, MaintenanceAction.DisableTask, "ScheduledDefrag");
            True(stop != null, "运行中的任务应产生 StopTask");
            True(disable != null, "应产生 DisableTask");
            True(stop.Order < disable.Order, "StopTask 必须排在 DisableTask 之前");
            Eq(false, stop.Critical);   // 停不下来不阻断，但会降级
        }

        private static void TestNightPlanDisabled()
        {
            QuietModeConfig cfg = DefaultConfig();
            cfg.Maintenance.Enabled = false;

            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, true, "Ready");

            MaintenancePlan plan = MaintenancePlanner.BuildNight(cfg, new BaselineSnapshot(), obs);
            Eq(0, plan.Steps.Count);

            MaintenanceVerification v = MaintenanceVerifier.VerifyNight(cfg, obs);
            Eq(MaintenanceVerdict.Ok, v.Verdict);
        }

        // ---------------- 夜间验证 ----------------

        private static void TestNightVerifyEnabledFails()
        {
            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, true, "Ready");
            SetSysMain(obs, true, 4, "Stopped");
            GoodNightRegistry(obs);

            MaintenanceVerification v = MaintenanceVerifier.VerifyNight(DefaultConfig(), obs);
            Eq(MaintenanceVerdict.Failed, v.Verdict);
            True(v.Problems.Exists(p => p.Contains("仍然处于启用状态")), "应指出任务仍启用");
        }

        private static void TestNightVerifyRunningDegrades()
        {
            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, false, "Running");
            SetSysMain(obs, true, 4, "Stopped");
            GoodNightRegistry(obs);

            MaintenanceVerification v = MaintenanceVerifier.VerifyNight(DefaultConfig(), obs);
            Eq(MaintenanceVerdict.Degraded, v.Verdict);
            True(v.Warnings.Exists(w => w.Contains("仍在运行")), "应指出任务仍在运行");
        }

        private static void TestNightVerifyAllGood()
        {
            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, false, "Disabled");
            SetSysMain(obs, true, 4, "Stopped");
            GoodNightRegistry(obs);

            MaintenanceVerification v = MaintenanceVerifier.VerifyNight(DefaultConfig(), obs);
            Eq(MaintenanceVerdict.Ok, v.Verdict);
        }

        // ---------------- 白天计划 ----------------

        private static void TestDayPlanEnables()
        {
            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, false, "Disabled");
            SetSysMain(obs, true, 4, "Stopped");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), BaselineAllEnabled(), obs);
            Eq(MaintenanceCatalog.NightDisableTargets.Count, CountSteps(plan, MaintenanceAction.EnableTask));
        }

        private static void TestDayPlanKeepsDisabled()
        {
            BaselineSnapshot baseline = BaselineAllEnabled();
            foreach (TaskSnapshot t in baseline.ScheduledTasks) t.Enabled = false;

            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, false, "Disabled");
            SetSysMain(obs, true, 4, "Stopped");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), baseline, obs);
            Eq(0, CountSteps(plan, MaintenanceAction.EnableTask));
        }

        private static void TestDayPlanRestoresDisabled()
        {
            BaselineSnapshot baseline = BaselineAllEnabled();
            foreach (TaskSnapshot t in baseline.ScheduledTasks) t.Enabled = false;

            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, true, "Ready");   // 被外部改回启用
            SetSysMain(obs, true, 4, "Stopped");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), baseline, obs);
            Eq(0, CountSteps(plan, MaintenanceAction.EnableTask));
            Eq(MaintenanceCatalog.NightDisableTargets.Count, CountSteps(plan, MaintenanceAction.DisableTask));
        }

        private static void TestDayPlanDeletesAbsentValue()
        {
            BaselineSnapshot baseline = new BaselineSnapshot();
            RegistrySnapshot r = new RegistrySnapshot();
            r.KeyPath = MaintenanceCatalog.MaintenanceRegistryKey;
            r.ValueName = MaintenanceCatalog.MaintenanceWakeValue;
            r.Exists = false;
            r.Kind = "None";
            baseline.Registry.Add(r);

            SystemObservation obs = EmptyObservation();
            SetSysMain(obs, true, 2, "Running");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), baseline, obs);
            MaintenanceStep s = FirstStep(plan, MaintenanceAction.DeleteRegistryValue, MaintenanceCatalog.MaintenanceWakeValue);
            True(s != null, "baseline 记录不存在时应生成删除步骤");
            Eq(0, CountSteps(plan, MaintenanceAction.SetRegistryValue));
        }

        private static void TestDayPlanRestoresSysMain()
        {
            BaselineSnapshot baseline = new BaselineSnapshot();
            ServiceSnapshot svc = new ServiceSnapshot();
            svc.ServiceName = MaintenanceCatalog.SysMainService;
            svc.StartValue = 2;
            svc.StartupType = "automatic";
            svc.Status = "Running";
            baseline.Services.Add(svc);

            SystemObservation obs = EmptyObservation();
            SetSysMain(obs, true, 4, "Stopped");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), baseline, obs);
            MaintenanceStep setStart = FirstStep(plan, MaintenanceAction.SetServiceStart, MaintenanceCatalog.SysMainService);
            True(setStart != null, "应恢复服务启动类型");
            Eq("2", setStart.DesiredValue);
            True(FirstStep(plan, MaintenanceAction.StartService, MaintenanceCatalog.SysMainService) != null,
                "baseline 记录当时在运行，应恢复运行");
        }

        // ---------------- 白天验证 ----------------

        private static void TestDayVerifyRegistryDrift()
        {
            BaselineSnapshot baseline = new BaselineSnapshot();
            RegistrySnapshot r = new RegistrySnapshot();
            r.KeyPath = MaintenanceCatalog.MaintenanceRegistryKey;
            r.ValueName = MaintenanceCatalog.MaintenanceBoundaryValue;
            r.Exists = true;
            r.Kind = "DWord";
            r.Value = "10";
            baseline.Registry.Add(r);
            baseline.Services.Add(new ServiceSnapshot { ServiceName = MaintenanceCatalog.SysMainService, StartValue = 2, Status = "Running" });

            SystemObservation obs = EmptyObservation();
            SetSysMain(obs, true, 2, "Running");
            AddRegistry(obs, MaintenanceCatalog.MaintenanceBoundaryValue, true, "23");

            MaintenanceVerification v = MaintenanceVerifier.VerifyDay(DefaultConfig(), baseline, obs);
            Eq(MaintenanceVerdict.Failed, v.Verdict);
        }

        private static void TestDayVerifyUnexpectedValue()
        {
            BaselineSnapshot baseline = new BaselineSnapshot();
            RegistrySnapshot r = new RegistrySnapshot();
            r.KeyPath = MaintenanceCatalog.MaintenanceRegistryKey;
            r.ValueName = MaintenanceCatalog.MaintenanceWakeValue;
            r.Exists = false;
            r.Kind = "None";
            baseline.Registry.Add(r);

            SystemObservation obs = EmptyObservation();
            AddRegistry(obs, MaintenanceCatalog.MaintenanceWakeValue, true, "0");
            SetSysMain(obs, true, 2, "Running");

            MaintenanceVerification v = MaintenanceVerifier.VerifyDay(DefaultConfig(), baseline, obs);
            Eq(MaintenanceVerdict.Failed, v.Verdict);
            True(v.Problems.Exists(p => p.Contains("原本不存在")), "应指出多余的注册表值");
        }

        private static void TestDayVerifyMissingTaskDegrades()
        {
            BaselineSnapshot baseline = BaselineAllEnabled();

            SystemObservation obs = EmptyObservation();
            SetAllNightTargets(obs, true, "Ready");   // 其余任务与 baseline 一致
            SetTask(obs, Def("ScheduledDefrag"), TaskPresence.NotPresent, null, "");
            SetSysMain(obs, true, 2, "Running");

            MaintenanceVerification v = MaintenanceVerifier.VerifyDay(DefaultConfig(), BaselineAllEnabled(), obs);
            Eq(MaintenanceVerdict.Degraded, v.Verdict);
            True(v.Warnings.Exists(w => w.Contains("找不到")), "应指出任务消失");

            MaintenancePlan plan = MaintenancePlanner.BuildDay(DefaultConfig(), baseline, obs);
            Eq(0, CountSteps(plan, MaintenanceAction.EnableTask));
            True(plan.Notes.Exists(n => n.Contains("无需恢复")), "消失的任务应在计划里被说明");
        }

        // ---------------- 状态语义 ----------------

        private static void TestStepStatusNotRun()
        {
            MaintenanceStep s = new MaintenanceStep();
            s.Order = 3;
            s.Action = MaintenanceAction.DisableTask;
            s.Target = @"\X\Y";
            s.Status = StepStatus.NotRun;
            s.Error = "维护切换需要管理员权限";

            JsonNode node = JsonNode.Parse(s.ToJson().ToJson(true));
            MaintenanceStep back = MaintenanceStep.FromJson(node.TryObject());
            Eq(StepStatus.NotRun, back.Status);
            True(back.ToLine().Contains("未执行"), "NotRun 必须显式显示为未执行，不能混同成功或跳过");
        }
    }
}
