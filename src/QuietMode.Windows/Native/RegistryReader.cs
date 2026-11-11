using System;
using System.Text;
using Microsoft.Win32;
using QuietMode.Core.Models;

namespace QuietMode.Windows.Native
{
    /// <summary>注册表值只读快照。值统一转成字符串，Kind 保真以便恢复时按原类型写回。</summary>
    public static class RegistryReader
    {
        public static RegistrySnapshot Read(string keyPath, string valueName, string module)
        {
            RegistrySnapshot s = new RegistrySnapshot();
            s.KeyPath = keyPath ?? "";
            s.ValueName = valueName ?? "";
            s.Module = module ?? "";

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key == null) { s.Exists = false; s.Kind = "None"; return s; }

                    object value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null && !ValueExists(key, valueName))
                    {
                        s.Exists = false;
                        s.Kind = "None";
                        return s;
                    }

                    s.Exists = true;
                    s.Kind = key.GetValueKind(valueName).ToString();
                    s.Value = Format(value, s.Kind);
                    return s;
                }
            }
            catch (Exception ex)
            {
                s.Exists = false;
                s.Kind = "Error";
                s.Value = ex.Message;
                return s;
            }
        }

        private static bool ValueExists(RegistryKey key, string name)
        {
            string[] names = key.GetValueNames();
            foreach (string n in names)
            {
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Format(object value, string kind)
        {
            if (value == null) return "";
            byte[] bytes = value as byte[];
            if (bytes != null)
            {
                StringBuilder sb = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            string[] multi = value as string[];
            if (multi != null) return string.Join("\n", multi);
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
