using System;
using System.Collections.Generic;
using System.IO;
using QuietMode.Core.Configuration;
using QuietMode.Core.Json;
using QuietMode.Core.Maintenance;
using QuietMode.Core.Models;
using QuietMode.Core.Processes;

namespace QuietMode.Windows
{
    /// <summary>一次夜间/白天维护切换的完整结果：计划、执行、复观测、验证。</summary>
    public sealed class MaintenanceRun
    {
        public string Phase = "";
        public bool Apply;
        public bool Ok;
        public string Error = "";

        public MaintenancePlan Plan;
        public SystemObservation Before;
        public SystemObservation After;
        public MaintenanceVerification Verification;
        public bool Elevated;
        public string RequestFile = "";
        public string ResultFile = "";
        public string StdErr = "";

        public SystemObservation Observation { get { return Apply && After != null ? After : Before; } }

        public string VerdictText
        {
            get { return Verification == null ? "Unknown" : Verification.VerdictText; }
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("phase", Phase ?? "");
            o.Set("apply", Apply);
            o.Set("ok", Ok);
            o.Set("elevated", Elevated);
            o.Set("verdict", Verification == null ? "Unknown" : Verification.Verdict.ToString());
            o.Set("verdictText", VerdictText);
            if (!string.IsNullOrEmpty(Error)) o.Set("error", Error);
            if (Plan != null) o.Set("plan", Plan.ToJson());
            if (Verification != null) o.Set("verification", Verification.ToJson());
            if (Observation != null) o.Set("observation", Observation.ToJson());
            return o;
        }
    }

    /// <summary>
    /// 维护管理控制器：C# 出计划 → PowerShell 落盘 → C# 重新观测并独立验证。
    /// 脚本说成功不算成功，只有复观测和 baseline 对上才算（规格 §4.2）。
    /// </summary>
    public static class MaintenanceController
    {
        public const string ScriptRelativePath = @"powershell\Maintenance\Invoke-MaintenanceOperation.ps1";
        public const int DefaultTimeoutMs = 180000;

        public static MaintenanceRun Run(QuietModePaths paths, QuietModeConfig cfg, BaselineSnapshot baseline,
                                         string phase, bool apply, int timeoutMs)
        {
            MaintenanceRun run = new MaintenanceRun();
            run.Phase = phase;
            run.Apply = apply;
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            if (cfg == null) { run.Error = "未提供配置"; return run; }
            if (baseline == null) baseline = new BaselineSnapshot();

            if (string.Equals(phase, "night", StringComparison.OrdinalIgnoreCase))
                run.Plan = MaintenancePlanner.BuildNight(cfg, baseline, null);
            else if (string.Equals(phase, "day", StringComparison.OrdinalIgnoreCase))
                run.Plan = MaintenancePlanner.BuildDay(cfg, baseline, null);
            else { run.Error = "未知阶段：" + phase + "（只支持 night / day）"; return run; }

            run.Before = MaintenanceProbe.Capture();
            run.Elevated = run.Before.Elevated;

            // 用真实观测重算一次，步骤才会带 StopTask 之类的条件动作
            run.Plan = string.Equals(phase, "night", StringComparison.OrdinalIgnoreCase)
                ? MaintenancePlanner.BuildNight(cfg, baseline, run.Before)
                : MaintenancePlanner.BuildDay(cfg, baseline, run.Before);

            if (!apply || run.Plan.Steps.Count == 0)
            {
                run.Verification = Verify(run.Plan.Phase, cfg, baseline, run.Before);
                run.Ok = run.Verification.Verdict == MaintenanceVerdict.Ok;
                if (!apply) run.Error = "";
                return run;
            }

            // ---- 交给脚本执行 ----
            string repo = RepoLocator.FindForExecutingAssembly();
            if (string.IsNullOrEmpty(repo)) { run.Error = "找不到仓库根目录，无法定位维护脚本"; return run; }
            string script = RepoLocator.Script(repo, ScriptRelativePath);
            if (script == null) { run.Error = "找不到维护脚本：" + Path.Combine(repo, ScriptRelativePath); return run; }

            try { paths.EnsureLayout(); }
            catch (Exception ex) { run.Error = "无法创建运行目录 " + paths.RunDir + "：" + ex.Message; return run; }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            string reqFile = Path.Combine(paths.RunDir, "maint-request-" + stamp + ".json");
            string resFile = Path.Combine(paths.RunDir, "maint-result-" + stamp + ".json");
            run.RequestFile = reqFile;
            run.ResultFile = resFile;

            JsonObject req = new JsonObject();
            req.Set("phase", run.Plan.Phase);
            req.Set("requestedBy", "qmctl");
            req.Set("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            JsonArray steps;
            req.SetArray("steps", out steps);
            foreach (MaintenanceStep s in run.Plan.Steps) steps.Add(s.ToJson());

            try { AtomicFile.WriteText(reqFile, req.ToJson(true) + "\n", null, null); }
            catch (Exception ex) { run.Error = "写入请求文件失败：" + ex.Message; return run; }

            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\""
                          + " -RequestFile \"" + reqFile + "\""
                          + " -ResultFile \"" + resFile + "\""
                          + " -Root \"" + paths.Root + "\"";

            ProcessResult pr = ProcessRunner.Run(ProcessRunner.PowerShellPath(), args, repo, timeoutMs);
            run.StdErr = pr.StdErr;

            if (!pr.Started) { run.Error = "无法启动 PowerShell：" + pr.StartError; return run; }
            if (pr.TimedOut) { run.Error = "维护脚本超时（" + timeoutMs + " ms）后被终止"; return run; }

            if (!File.Exists(resFile))
            {
                run.Error = "脚本未生成结果文件（退出码 " + pr.ExitCode + "）"
                            + (string.IsNullOrWhiteSpace(pr.StdErr) ? "" : "：" + ProcessRunner.Flatten(pr.StdErr));
                MarkNotRun(run, run.Error);
                // 结果文件缺失时仍要复观测：可能已经改了一半
                run.After = MaintenanceProbe.Capture();
                run.Verification = Verify(run.Plan.Phase, cfg, baseline, run.After);
                return run;
            }

            MergeStepResults(run, resFile);
            if (!string.IsNullOrEmpty(run.Error)) MarkNotRun(run, run.Error);

            run.After = MaintenanceProbe.Capture();
            run.Elevated = run.After.Elevated;
            run.Verification = Verify(run.Plan.Phase, cfg, baseline, run.After);
            run.Ok = run.Verification.Verdict == MaintenanceVerdict.Ok;
            if (!string.IsNullOrEmpty(run.Error)) run.Ok = false;
            return run;
        }

        private static void MarkNotRun(MaintenanceRun run, string reason)
        {
            string why = reason ?? "";
            int cut = why.IndexOf('\n');
            if (cut > 0) why = why.Substring(0, cut);
            if (why.Length > 160) why = why.Substring(0, 160) + "...";
            foreach (MaintenanceStep s in run.Plan.Steps)
            {
                if (s.Status == StepStatus.Pending || s.Status == StepStatus.Done)
                {
                    if (s.Status == StepStatus.Done) continue;
                    s.Status = StepStatus.NotRun;
                    s.Error = why;
                }
            }
        }

        private static MaintenanceVerification Verify(string phase, QuietModeConfig cfg, BaselineSnapshot baseline,
                                                      SystemObservation obs)
        {
            return string.Equals(phase, "night", StringComparison.OrdinalIgnoreCase)
                ? MaintenanceVerifier.VerifyNight(cfg, obs)
                : MaintenanceVerifier.VerifyDay(cfg, baseline, obs);
        }

        private static void MergeStepResults(MaintenanceRun run, string resFile)
        {
            JsonNode node;
            string parseError;
            string text;
            try { text = File.ReadAllText(resFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { run.Error = "读取结果文件失败：" + ex.Message; return; }
            if (!JsonNode.TryParse(text, out node, out parseError))
            {
                run.Error = "结果文件不是合法 JSON：" + parseError;
                return;
            }
            JsonObject root = node.TryObject();
            if (root == null) { run.Error = "结果文件顶层不是对象"; return; }

            JsonObject host = root.GetObject("host");
            if (host != null) run.Elevated = run.Elevated || host.GetBool("elevated", false);

            string scriptError = root.GetString("error", "");
            if (scriptError.Length > 0) run.Error = scriptError;

            JsonArray results = root.GetArray("steps");
            if (results == null) return;
            for (int i = 0; i < results.Count; i++)
            {
                JsonObject item = results[i].TryObject();
                if (item == null) continue;
                int order = item.GetInt("order", -1);
                MaintenanceStep target = null;
                foreach (MaintenanceStep s in run.Plan.Steps)
                {
                    if (s.Order == order) { target = s; break; }
                }
                if (target == null) continue;

                MaintenanceStep fromScript = MaintenanceStep.FromJson(item);
                target.Status = fromScript.Status;
                target.Error = fromScript.Error;
                target.ObservedAfter = fromScript.ObservedAfter;
            }
        }
    }
}
