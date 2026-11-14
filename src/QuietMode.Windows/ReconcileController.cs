using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuietMode.Core.Configuration;
using QuietMode.Core.Disks;
using QuietMode.Core.Json;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;
using QuietMode.Core.Processes;
using QuietMode.Core.Scheduling;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    /// <summary>一次 reconcile 的完整结果：决策、计划、执行、复观测、验证、状态写回。</summary>
    public sealed class ReconcileRun
    {
        public string Action = "";
        public string Trigger = "";
        public bool Apply;
        public bool Ok;
        public string Error = "";
        public bool Elevated;
        public Decision Decision;
        public WindowEvaluation Window;
        public ReconcilePlan Plan;
        public ReconcileVerification Verification;
        public List<ReconcileStepResult> WorkerResults = new List<ReconcileStepResult>();
        public string RequestFile = "";
        public string ResultFile = "";
        public string StdErr = "";
        public string StartUtc = "";
        public string EndUtc = "";
        public bool StateSaved;

        public string VerdictText
        {
            get { return Verification == null ? "Unknown" : Verification.VerdictText; }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("action", Action ?? "");
            o.Set("trigger", Trigger ?? "");
            o.Set("apply", Apply);
            o.Set("ok", Ok);
            o.Set("elevated", Elevated);
            o.Set("verdict", Verification == null ? "Unknown" : Verification.Verdict.ToString());
            o.Set("verdictText", VerdictText);
            o.Set("startUtc", StartUtc ?? "");
            o.Set("endUtc", EndUtc ?? "");
            o.Set("stateSaved", StateSaved);
            if (!string.IsNullOrEmpty(Error)) o.Set("error", Error);
            if (Decision != null) o.Set("decision", Decision.ToJson());
            if (Window != null)
            {
                JsonObject w = new JsonObject();
                w.Set("band", Window.Band.ToString());
                w.Set("nowMinutes", Window.NowMinutes);
                w.Set("nightStartMinutes", Window.NightStartMinutes);
                w.Set("dayStartMinutes", Window.DayStartMinutes);
                w.Set("effectiveDayStartMinutes", Window.EffectiveDayStartMinutes);
                w.Set("startupOverrideApplied", Window.StartupOverrideApplied);
                w.Set("reason", Window.Reason ?? "");
                o.Set("window", w);
            }
            if (Plan != null) o.Set("plan", Plan.ToJson());
            if (Verification != null) o.Set("verification", Verification.ToJson());
            if (!string.IsNullOrEmpty(RequestFile)) o.Set("requestFile", RequestFile);
            if (!string.IsNullOrEmpty(ResultFile)) o.Set("resultFile", ResultFile);
            JsonArray arr;
            o.SetArray("workerSteps", out arr);
            foreach (ReconcileStepResult r in WorkerResults) arr.Add(r.ToJson());
            return o;
        }
    }

    /// <summary>
    /// 唯一的调度控制器（规格 §6.1 / Phase 4）。
    ///
    /// v1 有两套并行闸门（NightGate 的 gate.ps1 管维护任务、DiskGate 的 diskgate.ps1 管磁盘），
    /// 各自跑各自的时间判断，互相覆盖状态；开机补偿也只做在其中一个里。
    /// v2 把「决策」和「执行」都收敛到这一条链：
    ///   StateMachine 决策 → ReconcilePlanner 出步骤 → Invoke-QuietModeWorker.ps1 执行
    ///   → C# 重新观测并独立验证 → 状态写回 state.json
    /// </summary>
    public static class ReconcileController
    {
        public const string ScriptRelativePath = @"powershell\Worker\Invoke-QuietModeWorker.ps1";
        public const int DefaultTimeoutMs = 300000;

        /// <summary>只做决策和计划，不碰任何系统状态。</summary>
        public static ReconcileRun Plan(QuietModePaths paths, QuietModeConfig cfg, BaselineSnapshot baseline,
                                        QuietModeState state, string action, string trigger, bool apply, int timeoutMs)
        {
            ReconcileRun run = new ReconcileRun();
            run.Apply = apply;
            run.Trigger = trigger ?? "cli";
            run.StartUtc = NowIso();

            if (cfg == null) { run.Error = "未提供配置"; run.EndUtc = NowIso(); return run; }
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            DateTime now = DateTime.Now;
            run.Window = ScheduleWindow.Evaluate(cfg.Schedule, now, SystemClock.Uptime());

            DecisionInput input = new DecisionInput();
            input.Schedule = cfg.Schedule;
            input.State = state ?? new QuietModeState();
            input.Window = run.Window;
            input.Trigger = run.Trigger;
            input.Now = now;
            input.LastRunFailed = StateMachine.IsLastRunFailed(input.State);
            // 抑制标记先对账到当前夜间窗口：窗口换了就自动松开，旧状态就补上窗口。
            StateMachine.NormalizeSuppression(input.State, cfg.Schedule, now);
            // 开机后第一次 reconcile：上次开机前留下的手动静音不在白天恢复。
            string manualExpired = StateMachine.ExpireStaleManualOverride(
                input.State, run.Window, DateTime.UtcNow, SystemClock.Uptime());
            run.Decision = StateMachine.Decide(input);
            if (manualExpired.Length > 0) run.Decision.Notes.Add(manualExpired);

            string resolved = ResolveAction(action, run.Decision);
            run.Action = resolved;

            // 维护步骤需要真实观测才能决定「要不要 StopTask」。
            SystemObservation obs = null;
            MaintenancePlan maint = null;
            if (resolved == "night" || resolved == "day" || resolved == "restore")
            {
                obs = MaintenanceProbe.Capture();
                run.Elevated = obs.Elevated;
                maint = resolved == "night"
                    ? MaintenancePlanner.BuildNight(cfg, baseline ?? new BaselineSnapshot(), obs)
                    : MaintenancePlanner.BuildDay(cfg, baseline ?? new BaselineSnapshot(), obs);
            }

            bool targetNight = run.Decision != null
                ? run.Decision.TargetNight
                : string.Equals(resolved, "night", StringComparison.OrdinalIgnoreCase);
            run.Plan = ReconcilePlanner.Build(resolved, run.Trigger, run.Decision == null ? "" : run.Decision.Reason,
                                              cfg, maint, targetNight);

            string asked = (action ?? "").Trim().ToLowerInvariant();
            if (asked.Length > 0 && asked != "auto" && asked != "reconcile"
                && run.Decision != null && run.Decision.Action != resolved)
            {
                run.Plan.Notes.Add("显式指定动作 " + resolved + "，覆盖自动决策（" + run.Decision.State + "）");
            }

            run.EndUtc = NowIso();
            return run;
        }

        /// <summary>执行一次 reconcile（apply=true 才会写系统状态）。无论有没有活干都留一行心跳。</summary>
        public static ReconcileRun Run(QuietModePaths paths, QuietModeConfig cfg, BaselineSnapshot baseline,
                                       QuietModeState state, string action, string trigger, bool apply, int timeoutMs)
        {
            ReconcileRun run = RunCore(paths, cfg, baseline, state, action, trigger, apply, timeoutMs);
            WriteHeartbeat(paths, run, trigger, apply);
            return run;
        }

        /// <summary>
        /// 心跳日志。决策为 none / verify 时根本不写请求文件、不写 worker.log，
        /// 于是「任务没触发」和「触发了但无事可做」在磁盘上完全同形 —— 这条日志就是分界。
        /// </summary>
        private static void WriteHeartbeat(QuietModePaths paths, ReconcileRun run, string trigger, bool apply)
        {
            if (paths == null || run == null) return;
            try
            {
                Directory.CreateDirectory(paths.LogDir);
                string path = paths.ReconcileLogPath;
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                }
                catch { }

                string verdict = run.Verification != null
                    ? run.Verification.Verdict.ToString()
                    : (string.IsNullOrEmpty(run.Error) ? "NotRun" : "Failed");
                string line = NowIso()
                              + " trigger=" + (trigger ?? "")
                              + " action=" + (string.IsNullOrEmpty(run.Action) ? "none" : run.Action)
                              + " apply=" + (apply ? "1" : "0")
                              + " verdict=" + verdict
                              + (run.Verification == null ? "" : " drift=" + run.Verification.Classification)
                              + (run.Decision == null ? "" : " state=" + run.Decision.State)
                              + (string.IsNullOrEmpty(run.Error) ? "" : " error=" + ProcessRunner.Flatten(run.Error))
                              + (run.Verification == null || run.Verification.Problems.Count == 0
                                    ? ""
                                    : " why=" + ProcessRunner.Flatten(run.Verification.Problems[0]))
                              + Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { }
        }

        private static ReconcileRun RunCore(QuietModePaths paths, QuietModeConfig cfg, BaselineSnapshot baseline,
                                           QuietModeState state, string action, string trigger, bool apply, int timeoutMs)
        {
            ReconcileRun run = Plan(paths, cfg, baseline, state, action, trigger, apply, timeoutMs);
            if (!string.IsNullOrEmpty(run.Error)) return run;
            if (cfg == null) return run;

            if (!apply || run.Action == "none" || run.Action == "verify")
            {
                ObserveAfter(run, cfg, baseline);
                run.Ok = run.Verification != null && run.Verification.Verdict == ReconcileVerdict.Ok;
                if (!apply && run.Action == "none") run.Ok = true;
                if (state != null && run.Decision != null) state.ControllerState = run.Decision.State.ToString();

                // 漂移修复：决策说「已经到位、不用动」，但回读存在可修复偏差。此时直接返回，
                // 偏差就会一直挂着（配置 20 分钟、电源方案 300 秒就是这种形态）。
                // 只对 ActionableDrift 重跑；Unknown 盲写可能把正常状态改坏，SafetyBlocked 不许碰设备。
                string repair = run.Decision == null
                    ? null
                    : ReconcileVerifier.RepairTarget(apply, run.Action, run.Decision.TargetNight, run.Verification);
                if (string.IsNullOrEmpty(repair))
                {
                    run.EndUtc = NowIso();
                    return run;
                }

                ReconcileRun forced = Plan(paths, cfg, baseline, state, repair, trigger, apply, timeoutMs);
                forced.Plan.Notes.Add("漂移修复：自动判定为「无需动作」但存在可修复偏差（"
                                      + DriftClassifier.Text(run.Verification.Classification) + "），按 "
                                      + repair + " 强制重跑");
                forced.Decision = forced.Decision ?? run.Decision;
                run = forced;
            }

            string repo = RepoLocator.FindForExecutingAssembly();
            if (string.IsNullOrEmpty(repo)) { run.Error = "找不到仓库根目录，无法定位 Worker 脚本"; return run; }
            string script = RepoLocator.Script(repo, ScriptRelativePath);
            if (script == null) { run.Error = "找不到 Worker 脚本：" + Path.Combine(repo, ScriptRelativePath); return run; }

            try { paths.EnsureLayout(); }
            catch (Exception ex) { run.Error = "无法创建运行目录 " + paths.RunDir + "：" + ex.Message; return run; }

            // run\ 是「普通用户可能写过东西」的目录：目录本身必须是普通目录，
            // 文件名必须不可预测，写请求必须独占新建 —— 三条缺一条就能被重定向。
            try { PrivilegedIo.EnsurePlainDirectory(paths.RunDir); }
            catch (Exception ex) { run.Error = "运行目录不可信：" + ex.Message; return run; }

            DateTime started = DateTime.Now;
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            string reqFile = Path.Combine(paths.RunDir, PrivilegedIo.UniqueFileName("reconcile-request", started, nonce));
            string resFile = Path.Combine(paths.RunDir, PrivilegedIo.UniqueFileName("reconcile-result", started, nonce));
            run.RequestFile = reqFile;
            run.ResultFile = resFile;

            JsonObject req = BuildRequest(cfg, run, paths);
            try { PrivilegedIo.WriteNewText(reqFile, req.ToJson(true) + "\n"); }
            catch (Exception ex) { run.Error = "写入请求文件失败：" + ex.Message; return run; }

            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\""
                          + " -RequestFile \"" + reqFile + "\""
                          + " -ResultFile \"" + resFile + "\""
                          + " -Root \"" + paths.Root + "\"";

            ProcessResult pr = ProcessRunner.Run(ProcessRunner.PowerShellPath(), args, repo, timeoutMs);
            run.StdErr = pr.StdErr;
            if (!pr.Started) { run.Error = "无法启动 PowerShell：" + pr.StartError; return run; }
            if (pr.TimedOut) { run.Error = "Worker 超时（" + timeoutMs + " ms）后被终止"; }

            if (PrivilegedIo.IsPlainExistingFile(resFile)) MergeWorkerResult(run, resFile);
            else if (File.Exists(resFile))
                run.Error = "结果文件是重解析点或不是普通文件，拒绝读取（可能是被替换的路径）";
            else if (string.IsNullOrEmpty(run.Error))
                run.Error = "Worker 未生成结果文件（退出码 " + pr.ExitCode + "）"
                            + (string.IsNullOrWhiteSpace(pr.StdErr) ? "" : "：" + ProcessRunner.Flatten(pr.StdErr));

            ObserveAfter(run, cfg, baseline);

            run.Ok = run.Verification != null && run.Verification.Verdict == ReconcileVerdict.Ok;
            if (!string.IsNullOrEmpty(run.Error)) run.Ok = false;

            PersistState(paths, run, state);
            run.EndUtc = NowIso();
            return run;
        }

        /// <summary>只回读当前状态，不改任何东西。verify 用。</summary>
        public static ReconcileVerification Observe(string action, QuietModeConfig cfg, BaselineSnapshot baseline)
        {
            ReconcileRun tmp = new ReconcileRun();
            tmp.Action = string.IsNullOrEmpty(action) ? "verify" : action;
            ObserveAfter(tmp, cfg, baseline);
            return tmp.Verification;
        }

        private static JsonObject BuildRequest(QuietModeConfig cfg, ReconcileRun run, QuietModePaths paths)
        {
            JsonObject req = new JsonObject();
            req.Set("action", run.Action);
            req.Set("trigger", run.Trigger);
            req.Set("root", paths.Root);
            req.Set("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            req.Set("requestedBy", "qmctl");

            if (run.Plan.Maintenance != null) req.Set("maintenance", run.Plan.Maintenance.ToJson());

            JsonArray targets;
            req.SetArray("diskTargets", out targets);
            if (cfg.Disks.Enabled)
            {
                foreach (DiskTarget t in cfg.Disks.Targets)
                {
                    if (t == null || !t.Enabled) continue;
                    JsonObject item = new JsonObject();
                    item.Set("uniqueId", t.UniqueId ?? "");
                    item.Set("serialNumber", t.SerialNumber ?? "");
                    item.Set("friendlyName", t.FriendlyName ?? "");
                    item.Set("sizeBytes", t.SizeBytes);
                    item.Set("busType", t.BusType ?? "");
                    item.Set("enabled", true);
                    targets.Add(item);
                }
            }
            req.Set("diskForce", cfg.Disks.ForceBusy);

            JsonObject sd = new JsonObject();
            sd.Set("enabled", cfg.SpinDown != null && cfg.SpinDown.Enabled);
            sd.Set("minutes", cfg.SpinDown == null ? 0 : cfg.SpinDown.Minutes);
            sd.Set("seconds", cfg.SpinDown == null ? 0 : cfg.SpinDown.Seconds);
            req.Set("spinDown", sd);

            JsonObject fan = new JsonObject();
            fan.Set("enabled", cfg.Fan.Enabled);
            fan.Set("ipcPath", cfg.Fan.IpcPath ?? "");
            fan.Set("controlExe", cfg.Fan.ControlExe ?? "");
            fan.Set("normalProfile", cfg.Fan.NormalProfile ?? "");
            fan.Set("silentProfile", cfg.Fan.SilentProfile ?? "");
            fan.Set("flagPath", cfg.Fan.FlagPath ?? "");
            req.Set("fan", fan);

            JsonArray steps;
            req.SetArray("steps", out steps);
            foreach (ReconcileStep s in run.Plan.Steps) steps.Add(s.ToJson());
            return req;
        }

        private static void ObserveAfter(ReconcileRun run, QuietModeConfig cfg, BaselineSnapshot baseline)
        {
            SystemObservation obs = MaintenanceProbe.Capture();
            run.Elevated = run.Elevated || obs.Elevated;

            List<DiskStateObservation> disks = new List<DiskStateObservation>();
            if (cfg.Disks.Enabled)
            {
                List<DiskSnapshot> inventory = DiskInventory.Query();
                foreach (DiskMatch m in DiskIdentityResolver.ResolveAll(cfg.Disks, inventory))
                {
                    DiskStateObservation d = new DiskStateObservation();
                    d.UniqueId = m.Target == null ? "" : m.Target.UniqueId;
                    d.SerialNumber = m.Target == null ? "" : m.Target.SerialNumber;
                    d.FriendlyName = m.Target == null ? "" : m.Target.FriendlyName;
                    d.Matched = m.Unique;
                    d.Error = m.Reason ?? "";
                    if (m.Unique)
                    {
                        d.DiskNumber = m.Disk.Number;
                        d.IsOffline = m.Disk.IsOffline;
                        d.FriendlyName = m.Disk.FriendlyName;
                    }
                    disks.Add(d);
                }
            }

            // 核对口径跟目标相位走，而不是跟 action 字符串走：action=none 时夜里仍是 night。
            string verifyAction = string.Equals(run.Action, "none", StringComparison.OrdinalIgnoreCase)
                ? (run.Decision != null && run.Decision.TargetNight ? "night" : "day")
                : run.Action;

            FanObservation fan = null;
            if (cfg.Fan.Enabled)
            {
                bool wantSilent = string.Equals(verifyAction, "night", StringComparison.OrdinalIgnoreCase)
                                  || verifyAction == "verify-night";
                fan = FanProbe.Read(cfg.Fan, wantSilent);
            }

            SpinDownObservation spin = null;
            if (cfg.SpinDown != null && cfg.SpinDown.Enabled)
            {
                PowerPlanSnapshot pp = PowerPlanReader.Capture();
                spin = new SpinDownObservation();
                spin.Enabled = true;
                spin.ExpectedSeconds = cfg.SpinDown.Seconds;
                spin.Captured = pp.Captured;
                spin.AcSeconds = pp.DiskIdleAcSeconds;
                spin.DcSeconds = pp.DiskIdleDcSeconds;
                spin.Error = pp.Error ?? "";
            }

            run.Verification = ReconcileVerifier.Verify(
                verifyAction, cfg, baseline, obs, disks, fan, spin);
        }

        private static void PersistState(QuietModePaths paths, ReconcileRun run, QuietModeState state)
        {
            if (state == null || paths == null) return;
            try
            {
                string verdict = run.Verification == null ? "Unknown" : run.Verification.Verdict.ToString();
                bool nightOk = string.Equals(run.Action, "night", StringComparison.OrdinalIgnoreCase)
                               && run.Verification != null && run.Verification.Verdict == ReconcileVerdict.Ok;
                // 先把「为什么」写进状态，再写 verdict：状态文件必须能自己解释失败，
                // 而不是显示 NightActive 却什么警告都没有。
                state.LastDrift = run.Verification == null ? "" : run.Verification.Classification.ToString();
                state.LastProblems = run.Verification == null
                    ? new List<string>()
                    : new List<string>(run.Verification.Problems.Count > 20
                        ? run.Verification.Problems.GetRange(0, 20)
                        : run.Verification.Problems);
                StateMachine.ApplyRun(state, run.Action, verdict, run.Error, DateTime.UtcNow, nightOk);
                // 写「跑完之后停在哪」，不是「跑之前打算进哪」：照抄决策值会让 state.json
                // 整夜停在 EnteringNight、整个白天停在 EnteringDay，与实机相位差一格。
                if (run.Decision != null)
                {
                    state.ControllerState = ControllerStates.Settle(
                        run.Action,
                        run.Verification != null && run.Verification.Verdict == ReconcileVerdict.Ok,
                        run.Decision.State).ToString();
                }
                new StateStore(paths).Save(state);
                run.StateSaved = true;
            }
            catch (Exception ex)
            {
                run.Error = (string.IsNullOrEmpty(run.Error) ? "" : run.Error + "；") + "状态写回失败：" + ex.Message;
            }
        }

        private static string ResolveAction(string requested, Decision decision)
        {
            string a = (requested ?? "").Trim().ToLowerInvariant();
            if (a == "" || a == "auto" || a == "reconcile")
                return decision == null ? "none" : decision.Action;
            if (a == "enter-night" || a == "enternight" || a == "night") return "night";
            if (a == "enter-day" || a == "enterday" || a == "day") return "day";
            if (a == "restore") return "restore";
            if (a == "verify" || a == "status") return decision == null || !decision.NeedsWork ? "verify" : decision.Action;
            return "verify";
        }

        private static void MergeWorkerResult(ReconcileRun run, string resFile)
        {
            string text;
            try { text = File.ReadAllText(resFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { run.Error = "读取结果文件失败：" + ex.Message; return; }

            JsonNode node;
            string parseError;
            if (!JsonNode.TryParse(text, out node, out parseError))
            {
                run.Error = "Worker 结果不是合法 JSON：" + parseError;
                return;
            }
            JsonObject root = node.TryObject();
            if (root == null) { run.Error = "Worker 结果顶层不是对象"; return; }

            JsonObject host = root.GetObject("host");
            if (host != null) run.Elevated = run.Elevated || host.GetBool("elevated", false);

            string err = root.GetString("error", "");
            if (err.Length > 0) run.Error = err;

            JsonArray steps = root.GetArray("steps");
            if (steps != null)
            {
                for (int i = 0; i < steps.Count; i++)
                {
                    JsonObject item = steps[i].TryObject();
                    if (item == null) continue;
                    ReconcileStepResult r = ReconcileStepResult.FromJson(item);
                    run.WorkerResults.Add(r);

                    int order = r.Order;
                    foreach (ReconcileStep s in run.Plan.Steps)
                    {
                        if (s.Order != order) continue;
                        s.Status = r.Status;
                        s.Error = r.Error;
                        break;
                    }
                }
            }
        }

        /// <summary>Environment.TickCount 是系统启动以来的毫秒数，即机器开机时长。</summary>
        private static string NowIso()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
