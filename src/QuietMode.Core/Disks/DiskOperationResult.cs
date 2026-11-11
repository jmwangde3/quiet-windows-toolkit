using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Disks
{
    /// <summary>磁盘操作的错误码。UI 与脚本按码判断，不解析文案。</summary>
    public static class DiskErrorCode
    {
        public const string Ok = "OK";
        public const string NoTarget = "NO_TARGET";
        public const string NoIdentity = "NO_IDENTITY";
        public const string IdentityNotFound = "IDENTITY_NOT_FOUND";
        public const string IdentityAmbiguous = "IDENTITY_AMBIGUOUS";
        public const string IdentityConflict = "IDENTITY_CONFLICT";
        public const string SafetyBlocked = "SAFETY_BLOCKED";
        public const string Busy = "BUSY";
        public const string AlreadyInState = "ALREADY_IN_STATE";
        public const string ElevationRequired = "ELEVATION_REQUIRED";
        public const string SetFailed = "SET_FAILED";
        public const string VerifyFailed = "VERIFY_FAILED";
        public const string Timeout = "TIMEOUT";
        public const string ScriptError = "SCRIPT_ERROR";
        public const string NotSupported = "NOT_SUPPORTED";
        public const string ProbeFailed = "PROBE_FAILED";
    }

    /// <summary>
    /// 结构化磁盘操作结果（规格 §3.4）。禁止只报「已请求脱机」而不回读实际状态：
    /// Success 只有在 ActualState 与 RequestedState 一致、且 Verified=true 时才为 true。
    /// </summary>
    public sealed class DiskOperationResult
    {
        public string Operation = "";           // offline / online / verify
        public string ErrorCode = DiskErrorCode.Ok;
        public string ErrorMessage = "";
        public bool Success;
        public bool Verified;

        // ---- 规格要求的字段 ----
        public string UniqueId = "";
        public string SerialNumber = "";
        public string FriendlyName = "";
        public int CurrentDiskNumber = -1;
        public string PreviousState = "unknown";   // online / offline / unknown
        public string RequestedState = "unknown";
        public string ActualState = "unknown";
        public string Timestamp = "";

        public List<string> Steps = new List<string>();
        public List<string> Warnings = new List<string>();

        public static DiskOperationResult Make(string operation)
        {
            DiskOperationResult r = new DiskOperationResult();
            r.Operation = operation;
            r.Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            return r;
        }

        public DiskOperationResult Fail(string code, string message)
        {
            ErrorCode = code;
            ErrorMessage = message ?? "";
            Success = false;
            Verified = false;
            return this;
        }

        /// <summary>只有回读一致才算成功；这里再次强制这个不变式。</summary>
        public DiskOperationResult Complete()
        {
            bool stateMatches = string.Equals(ActualState, RequestedState, StringComparison.OrdinalIgnoreCase)
                                && RequestedState != "unknown";
            Success = stateMatches && ErrorCode == DiskErrorCode.Ok;
            Verified = stateMatches;
            if (!stateMatches && ErrorCode == DiskErrorCode.Ok)
            {
                ErrorCode = DiskErrorCode.VerifyFailed;
                ErrorMessage = "请求状态 " + RequestedState + "，实际状态 " + ActualState;
            }
            return this;
        }

        public DiskOperationResult Step(string text)
        {
            Steps.Add(text);
            return this;
        }

        public string ToText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append(Success ? "OK " : "FAIL ").Append(Operation.ToUpperInvariant());
            sb.Append("  disk=#").Append(CurrentDiskNumber);
            if (!string.IsNullOrEmpty(UniqueId)) sb.Append("  uniqueId=").Append(UniqueId);
            sb.Append('\n').Append("  ").Append(PreviousState).Append(" -> ").Append(RequestedState)
              .Append("  actual=").Append(ActualState).Append(Verified ? " (已验证)" : " (未验证)");
            if (!string.IsNullOrEmpty(ErrorCode) && ErrorCode != DiskErrorCode.Ok)
                sb.Append('\n').Append("  ").Append(ErrorCode).Append(": ").Append(ErrorMessage);
            foreach (string s in Steps) sb.Append('\n').Append("  · ").Append(s);
            foreach (string w in Warnings) sb.Append('\n').Append("  ! ").Append(w);
            return sb.ToString();
        }

        public JsonObject ToJson()
        {
            JsonObject o = new JsonObject();
            o.Set("operation", Operation ?? "");
            o.Set("success", Success);
            o.Set("verified", Verified);
            o.Set("errorCode", ErrorCode ?? "");
            o.Set("errorMessage", ErrorMessage ?? "");
            o.Set("uniqueId", UniqueId ?? "");
            o.Set("serialNumber", SerialNumber ?? "");
            o.Set("friendlyName", FriendlyName ?? "");
            o.Set("currentDiskNumber", CurrentDiskNumber);
            o.Set("previousState", PreviousState ?? "unknown");
            o.Set("requestedState", RequestedState ?? "unknown");
            o.Set("actualState", ActualState ?? "unknown");
            o.Set("timestamp", Timestamp ?? "");
            JsonArray steps;
            o.SetArray("steps", out steps);
            foreach (string s in Steps) steps.Add(s);
            JsonArray warns;
            o.SetArray("warnings", out warns);
            foreach (string s in Warnings) warns.Add(s);
            return o;
        }

        public static DiskOperationResult FromJson(JsonObject o)
        {
            DiskOperationResult r = Make(o.GetString("operation", ""));
            r.Success = o.GetBool("success", false);
            r.Verified = o.GetBool("verified", false);
            r.ErrorCode = o.GetString("errorCode", DiskErrorCode.ScriptError);
            r.ErrorMessage = o.GetString("errorMessage", "");
            r.UniqueId = o.GetString("uniqueId", "");
            r.SerialNumber = o.GetString("serialNumber", "");
            r.FriendlyName = o.GetString("friendlyName", "");
            r.CurrentDiskNumber = o.GetInt("currentDiskNumber", -1);
            r.PreviousState = o.GetString("previousState", "unknown");
            r.RequestedState = o.GetString("requestedState", "unknown");
            r.ActualState = o.GetString("actualState", "unknown");
            r.Timestamp = o.GetString("timestamp", "");
            JsonArray steps = o.GetArray("steps");
            if (steps != null) for (int i = 0; i < steps.Count; i++) r.Steps.Add(steps[i].AsString());
            JsonArray warns = o.GetArray("warnings");
            if (warns != null) for (int i = 0; i < warns.Count; i++) r.Warnings.Add(warns[i].AsString());
            return r;
        }
    }
}
