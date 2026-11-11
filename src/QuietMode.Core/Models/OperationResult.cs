using System;
using System.Collections.Generic;
using QuietMode.Core.Json;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// 单步操作结果。所有会改系统状态的方法统一返回它，禁止只报「已请求」。
    /// </summary>
    public sealed class OperationResult
    {
        public bool Ok;
        public string Action = "";
        public string Detail = "";
        public bool Verified;
        public List<string> Notes = new List<string>();

        public static OperationResult Success(string action, string detail)
        {
            OperationResult r = new OperationResult();
            r.Ok = true;
            r.Action = action;
            r.Detail = detail ?? "";
            r.Verified = true;
            return r;
        }

        public static OperationResult Verified2(string action, string detail)
        {
            OperationResult r = Success(action, detail);
            r.Verified = true;
            return r;
        }

        public static OperationResult Requested(string action, string detail)
        {
            OperationResult r = new OperationResult();
            r.Ok = true;
            r.Action = action;
            r.Detail = detail ?? "";
            r.Verified = false;
            return r;
        }

        public static OperationResult Fail(string action, string detail)
        {
            OperationResult r = new OperationResult();
            r.Ok = false;
            r.Action = action;
            r.Detail = detail ?? "";
            r.Verified = false;
            return r;
        }

        public OperationResult WithNote(string note)
        {
            Notes.Add(note);
            return this;
        }

        public override string ToString()
        {
            return (Ok ? "OK" : "FAIL") + " " + Action + (Verified ? "" : " (未验证)") + " — " + Detail;
        }
    }
}
