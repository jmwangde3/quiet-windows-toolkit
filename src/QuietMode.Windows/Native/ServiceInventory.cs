using System;
using System.ServiceProcess;
using Microsoft.Win32;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    public static class ServiceInventory
    {
        private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

        public static ServiceSnapshot Query(string serviceName)
        {
            ServiceSnapshot s = new ServiceSnapshot();
            s.ServiceName = serviceName ?? "";
            if (string.IsNullOrWhiteSpace(serviceName)) return s;

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(ServicesKey + serviceName))
                {
                    if (key != null)
                    {
                        object display = key.GetValue("DisplayName");
                        s.DisplayName = display == null ? "" : Convert.ToString(display).Trim();
                        object start = key.GetValue("Start");
                        if (start != null)
                        {
                            s.StartValue = Convert.ToInt32(start);
                            s.StartupType = StartupTypeName(s.StartValue);
                        }
                        object delayed = key.GetValue("DelayedAutoStart");
                        if (delayed != null && Convert.ToInt32(delayed) == 1
                            && string.Equals(s.StartupType, "automatic", StringComparison.Ordinal))
                        {
                            s.StartupType = "automatic-delayed";
                        }
                    }
                    else
                    {
                        s.StartupType = "missing";
                    }
                }
            }
            catch (Exception ex)
            {
                s.StartupType = "unknown";
                s.DisplayName = "读取注册表失败：" + ex.Message;
            }

            try
            {
                using (ServiceController sc = new ServiceController(serviceName))
                {
                    s.Status = sc.Status.ToString();
                }
            }
            catch
            {
                s.Status = "unknown";
            }
            return s;
        }

        public static string StartupTypeName(int start)
        {
            switch (start)
            {
                case 0: return "boot";
                case 1: return "system";
                case 2: return "automatic";
                case 3: return "manual";
                case 4: return "disabled";
                default: return "unknown(" + start + ")";
            }
        }
    }
}
