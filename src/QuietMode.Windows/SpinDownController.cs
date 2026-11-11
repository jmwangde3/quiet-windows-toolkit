using System;
using System.Collections.Generic;
using QuietMode.Core.Json;
using QuietMode.Core.Processes;
using QuietMode.Windows.Native;

namespace QuietMode.Windows
{
    /// <summary>一次 DISKIDLE 设置的结果。Ok 只在「回读到目标值」时为 true。</summary>
    public sealed class SpinDownResult
    {
        public bool Apply;
        public bool Ok;
        public bool Elevated;
        public string ErrorCode = "";
        public string Error = "";
        public int RequestedSeconds;
        public int ObservedAcSeconds = -1;
        public int ObservedDcSeconds = -1;
        public List<string> Commands = new List<string>();
        public string StdErr = "";

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("apply", Apply);
            o.Set("ok", Ok);
            o.Set("elevated", Elevated);
            o.Set("errorCode", ErrorCode ?? "");
            o.Set("error", Error ?? "");
            o.Set("requestedSeconds", RequestedSeconds);
            o.Set("observedAcSeconds", ObservedAcSeconds);
            o.Set("observedDcSeconds", ObservedDcSeconds);
            JsonArray arr;
            o.SetArray("commands", out arr);
            foreach (string c in Commands) arr.Add(c);
            if (!string.IsNullOrEmpty(StdErr)) o.Set("stdErr", StdErr);
            return o;
        }

        public string ToText()
        {
            if (!Apply)
            {
                return "演练（未写入）：目标 DISKIDLE = " + RequestedSeconds + " 秒\n  "
                       + string.Join("\n  ", Commands.ToArray());
            }
            if (Ok)
                return "DISKIDLE 已设为 " + RequestedSeconds + " 秒（回读 AC=" + ObservedAcSeconds
                       + "s DC=" + ObservedDcSeconds + "s）";
            if (string.Equals(ErrorCode, "ELEVATION_REQUIRED", StringComparison.OrdinalIgnoreCase))
                return "DISKIDLE 未改动：" + Error + "（用管理员身份重跑即可）";
            return "DISKIDLE 未生效：" + (string.IsNullOrEmpty(Error) ? ErrorCode : Error)
                   + "（回读 AC=" + ObservedAcSeconds + "s DC=" + ObservedDcSeconds + "s）";
        }
    }

    /// <summary>
    /// 硬盘空闲停转（DISKIDLE）的执行器。v1 是托盘里拼一段 PowerShell 提权脚本，
    /// v2 收到这里：命令由 C# 出，执行走 powercfg，结论只认回读值。
    ///
    /// 与静音模式的关系（2026-11-12 修正）：`spinDown.enabled=true` 时它是
    /// reconcile 链上的一步（night/day 都跑），由 ReconcilePlanner 排进计划、
    /// worker 调 Invoke-QmSpinDown 写入、C# 复观测回读校验。
    /// `enabled=false` 时整步标 skipped，一个字节都不写 —— 手动设置因此得以保留。
    /// 之所以早先不放进计划，是因为当时没有回读校验，写坏无人知；现在有了。
    /// </summary>
    public static class SpinDownController
    {
        public const int MaxSeconds = 10800;   // 180 分钟，与 v1 上限一致

        public static SpinDownResult Apply(int minutes, bool apply)
        {
            SpinDownResult r = new SpinDownResult();
            r.Apply = apply;
            if (minutes < 0) minutes = 0;
            if (minutes * 60 > MaxSeconds) minutes = MaxSeconds / 60;
            r.RequestedSeconds = minutes * 60;
            r.Elevated = StatusProbe.IsElevated();

            string sec = r.RequestedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.Commands.Add("powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE " + sec);
            r.Commands.Add("powercfg /setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE " + sec);
            r.Commands.Add("powercfg /setactive SCHEME_CURRENT");

            if (!apply) return r;
            if (!r.Elevated)
            {
                r.ErrorCode = "ELEVATION_REQUIRED";
                r.Error = "修改 DISKIDLE 需要管理员权限";
                return r;
            }

            try
            {
                foreach (string args in new string[]
                {
                    "/setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE " + sec,
                    "/setdcvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE " + sec,
                    "/setactive SCHEME_CURRENT"
                })
                {
                    ProcessResult pr = ProcessRunner.Run("powercfg.exe", args, null, 20000);
                    if (!pr.Started)
                    {
                        r.ErrorCode = "POWERCFG_FAILED";
                        r.Error = "无法启动 powercfg：" + pr.StartError;
                        return r;
                    }
                    if (pr.TimedOut)
                    {
                        r.ErrorCode = "POWERCFG_FAILED";
                        r.Error = "powercfg 超时：" + args;
                        return r;
                    }
                    if (pr.ExitCode != 0)
                    {
                        r.ErrorCode = "POWERCFG_FAILED";
                        r.Error = "powercfg 退出码 " + pr.ExitCode + "：" + args;
                        r.StdErr = ProcessRunner.Flatten(pr.StdErr);
                        return r;
                    }
                }
            }
            catch (Exception ex)
            {
                r.ErrorCode = "POWERCFG_FAILED";
                r.Error = "powercfg 异常：" + ex.Message;
                return r;
            }

            // 独立回读：优先 powercfg /query（与写入不同的读取路径），失败再读注册表。
            int ac, dc;
            string err;
            bool read = PowerPlanReader.TryReadFromPowerCfg(out ac, out dc, out err);
            if (!read) read = PowerPlanReader.TryReadFromRegistry(PowerPlanReader.ReadActiveSchemeGuid(), out ac, out dc, out err);
            if (!read)
            {
                r.ErrorCode = "VERIFY_UNREADABLE";
                r.Error = "写入后无法回读 DISKIDLE：" + err;
                return r;
            }

            r.ObservedAcSeconds = ac;
            r.ObservedDcSeconds = dc;
            if (ac == r.RequestedSeconds && dc == r.RequestedSeconds)
            {
                r.Ok = true;
                r.ErrorCode = "OK";
                return r;
            }

            r.ErrorCode = "VERIFY_MISMATCH";
            r.Error = "回读与目标不一致（目标 " + r.RequestedSeconds + "s，AC=" + ac + "s DC=" + dc + "s）"
                      + "；电源方案可能把该值夹到支持范围内";
            return r;
        }
    }
}
