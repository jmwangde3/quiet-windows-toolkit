using System;
using System.Collections.Generic;
using System.IO;
using QuietMode.Core.Configuration;
using QuietMode.Core.Disks;
using QuietMode.Core.Json;
using QuietMode.Core.Models;
using QuietMode.Core.Processes;

namespace QuietMode.Windows
{
    /// <summary>一次磁盘操作的批量结果（可能包含多个目标）。</summary>
    public sealed class DiskOperationBatch
    {
        public bool Ok;
        public string Error = "";
        public List<DiskOperationResult> Results = new List<DiskOperationResult>();
        public bool Elevated;
        public ProcessResult Process;
        public string RequestFile = "";
        public string ResultFile = "";
        public string StdErr = "";

        public bool AnyFailed
        {
            get
            {
                foreach (DiskOperationResult r in Results) if (!r.Success) return true;
                return Results.Count == 0;
            }
        }
    }

    /// <summary>
    /// 磁盘操作控制器：把结构化请求写成 JSON 文件交给 PowerShell 执行，再读回结构化结果。
    /// 绝不把配置值拼进脚本正文（规格 §7.3），也绝不根据脚本的标准输出「猜」状态。
    /// </summary>
    public static class DiskController
    {
        public const string ScriptRelativePath = @"powershell\Disks\Invoke-DiskOperation.ps1";
        public const int DefaultTimeoutMs = 180000;

        public static DiskOperationBatch Run(QuietModePaths paths, string operation,
                                             IList<DiskTarget> targets, bool apply, bool force,
                                             int timeoutMs)
        {
            DiskOperationBatch batch = new DiskOperationBatch();
            if (paths == null) { batch.Error = "未提供路径配置"; return batch; }
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            string repo = RepoLocator.FindForExecutingAssembly();
            if (string.IsNullOrEmpty(repo))
            {
                batch.Error = "找不到仓库根目录（QuietMode.sln），无法定位 powershell/Disks/Invoke-DiskOperation.ps1";
                return batch;
            }
            string script = RepoLocator.Script(repo, ScriptRelativePath);
            if (script == null)
            {
                batch.Error = "找不到磁盘脚本：" + Path.Combine(repo, ScriptRelativePath);
                return batch;
            }

            try { paths.EnsureLayout(); }
            catch (Exception ex)
            {
                batch.Error = "无法创建运行目录 " + paths.RunDir + "：" + ex.Message;
                return batch;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            string reqFile = Path.Combine(paths.RunDir, "disk-request-" + stamp + ".json");
            string resFile = Path.Combine(paths.RunDir, "disk-result-" + stamp + ".json");
            batch.RequestFile = reqFile;
            batch.ResultFile = resFile;

            JsonObject req = new JsonObject();
            req.Set("operation", operation ?? "verify");
            req.Set("apply", apply);
            req.Set("force", force);
            req.Set("requestedBy", "qmctl");
            req.Set("timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            JsonArray arr;
            req.SetArray("targets", out arr);
            if (targets != null)
            {
                foreach (DiskTarget t in targets)
                {
                    JsonObject item;
                    arr.AddObject(out item);
                    item.Set("uniqueId", t.UniqueId ?? "");
                    item.Set("serialNumber", t.SerialNumber ?? "");
                    item.Set("friendlyName", t.FriendlyName ?? "");
                    item.Set("sizeBytes", t.SizeBytes);
                    item.Set("busType", t.BusType ?? "");
                }
            }

            try
            {
                AtomicFile.WriteText(reqFile, req.ToJson(true) + "\n", null, null);
            }
            catch (Exception ex)
            {
                batch.Error = "写入请求文件失败：" + ex.Message;
                return batch;
            }

            string args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\""
                          + " -RequestFile \"" + reqFile + "\""
                          + " -ResultFile \"" + resFile + "\""
                          + " -Root \"" + paths.Root + "\""
                          + " -ConfigPath \"" + paths.ConfigPath + "\"";

            ProcessResult pr = ProcessRunner.Run(ProcessRunner.PowerShellPath(), args, repo, timeoutMs);
            batch.Process = pr;
            batch.StdErr = pr.StdErr;

            if (!pr.Started)
            {
                batch.Error = "无法启动 PowerShell：" + pr.StartError;
                return batch;
            }
            if (pr.TimedOut)
            {
                batch.Error = "磁盘脚本超时（" + timeoutMs + " ms）后被终止";
                return batch;
            }

            if (!File.Exists(resFile))
            {
                batch.Error = "脚本未生成结果文件（退出码 " + pr.ExitCode + "）"
                              + (string.IsNullOrWhiteSpace(pr.StdErr) ? "" : "：" + ProcessRunner.Flatten(pr.StdErr));
                return batch;
            }

            JsonNode node;
            string parseError;
            string text;
            try { text = File.ReadAllText(resFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { batch.Error = "读取结果文件失败：" + ex.Message; return batch; }

            if (!JsonNode.TryParse(text, out node, out parseError))
            {
                batch.Error = "结果文件不是合法 JSON：" + parseError;
                return batch;
            }
            JsonObject root = node.TryObject();
            if (root == null) { batch.Error = "结果文件顶层不是对象"; return batch; }

            JsonObject host = root.GetObject("host");
            if (host != null) batch.Elevated = host.GetBool("elevated", false);

            JsonArray results = root.GetArray("results");
            if (results != null)
            {
                for (int i = 0; i < results.Count; i++)
                {
                    JsonObject item = results[i].TryObject();
                    if (item == null) continue;
                    batch.Results.Add(DiskOperationResult.FromJson(item));
                }
            }

            batch.Ok = batch.Results.Count > 0 && !batch.AnyFailed;
            if (batch.Results.Count == 0) batch.Error = "结果文件中没有任何磁盘结果";
            return batch;
        }
    }
}
