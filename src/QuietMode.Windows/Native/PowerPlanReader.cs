using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    /// <summary>
    /// 电源方案只读查询。优先读注册表（确定性强、不受控制台编码影响），
    /// 读不到时回落到 powercfg /query 并对十六进制索引做兜底解析。
    /// </summary>
    public static class PowerPlanReader
    {
        private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        public const string DiskSubGroup = "0012ee47-9041-4b5d-9b77-535fba8b1442";
        public const string DiskIdleSetting = "6738e2c4-e8a5-4a42-b16a-e040e769756e";

        public static PowerPlanSnapshot Capture()
        {
            PowerPlanSnapshot p = new PowerPlanSnapshot();
            p.ActiveSchemeGuid = ReadActiveSchemeGuid();

            int ac, dc;
            string error;
            if (TryReadFromRegistry(p.ActiveSchemeGuid, out ac, out dc, out error))
            {
                p.Captured = true;
                p.DiskIdleAcSeconds = ac;
                p.DiskIdleDcSeconds = dc;
                return p;
            }

            int pac, pdc;
            string pError;
            if (TryReadFromPowerCfg(out pac, out pdc, out pError))
            {
                p.Captured = true;
                p.DiskIdleAcSeconds = pac;
                p.DiskIdleDcSeconds = pdc;
                p.Error = "注册表未取到（" + error + "），已用 powercfg 兜底";
                return p;
            }

            p.Captured = false;
            p.DiskIdleAcSeconds = -1;
            p.DiskIdleDcSeconds = -1;
            p.Error = "无法读取 DISKIDLE：" + error + " / " + pError;
            return p;
        }

        public static string ReadActiveSchemeGuid()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PowerSchemesKey))
                {
                    if (key == null) return "";
                    object v = key.GetValue("ActivePowerScheme");
                    return v == null ? "" : Convert.ToString(v).Trim();
                }
            }
            catch { return ""; }
        }

        public static bool TryReadFromRegistry(string schemeGuid, out int acSeconds, out int dcSeconds, out string error)
        {
            acSeconds = dcSeconds = -1;
            error = "";
            if (string.IsNullOrWhiteSpace(schemeGuid))
            {
                error = "活动电源方案 GUID 为空";
                return false;
            }

            string basePath = PowerSchemesKey + "\\" + schemeGuid + "\\" + DiskSubGroup + "\\" + DiskIdleSetting;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(basePath))
                {
                    if (key == null)
                    {
                        error = "缺少键 " + basePath;
                        return false;
                    }

                    object a = key.GetValue("ACSettingIndex");
                    object d = key.GetValue("DCSettingIndex");
                    if (a == null && d == null)
                    {
                        error = "AC/DCSettingIndex 均不存在";
                        return false;
                    }
                    if (a != null) acSeconds = Convert.ToInt32(a);
                    if (d != null) dcSeconds = Convert.ToInt32(d);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool TryReadFromPowerCfg(out int acSeconds, out int dcSeconds, out string error)
        {
            acSeconds = dcSeconds = -1;
            error = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powercfg.exe", "/query SCHEME_CURRENT SUB_DISK DISKIDLE");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.Default;

                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(10000);

                    if (p.ExitCode != 0)
                    {
                        error = "powercfg 退出码 " + p.ExitCode + " " + err.Trim();
                        return false;
                    }

                    List<int> hex = new List<int>();
                    foreach (Match m in Regex.Matches(output, "0x([0-9a-fA-F]{1,8})"))
                    {
                        int v;
                        if (int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out v)) hex.Add(v);
                    }
                    // powercfg 输出顺序：最小 / 最大 / 增量 / 当前交流 / 当前直流
                    if (hex.Count < 2)
                    {
                        error = "powercfg 输出中找不到当前索引";
                        return false;
                    }
                    acSeconds = hex[hex.Count - 2];
                    dcSeconds = hex[hex.Count - 1];
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
