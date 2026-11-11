using System;
using Microsoft.Win32;

namespace QuietMode.Windows.Native
{
    public static class WindowsVersionInfo
    {
        private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public static string Build()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey))
                {
                    if (key == null) return "";
                    object build = key.GetValue("CurrentBuild");
                    object ubr = key.GetValue("UBR");
                    string b = build == null ? "" : Convert.ToString(build).Trim();
                    if (ubr != null && b.Length > 0) b = b + "." + Convert.ToString(ubr).Trim();
                    return b;
                }
            }
            catch { return ""; }
        }

        public static string ProductName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey))
                {
                    if (key == null) return "";
                    object v = key.GetValue("ProductName");
                    return v == null ? "" : Convert.ToString(v).Trim();
                }
            }
            catch { return ""; }
        }

        public static string DisplayVersion()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey))
                {
                    if (key == null) return "";
                    object v = key.GetValue("DisplayVersion");
                    return v == null ? "" : Convert.ToString(v).Trim();
                }
            }
            catch { return ""; }
        }

        public static bool IsElevated()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                    return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static string UserName()
        {
            try { return Environment.UserDomainName + "\\" + Environment.UserName; }
            catch { return Environment.UserName; }
        }
    }
}
