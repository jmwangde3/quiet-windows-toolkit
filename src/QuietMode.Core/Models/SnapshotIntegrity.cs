using System;
using QuietMode.Core.Configuration;
using QuietMode.Core.Models;

namespace QuietMode.Core.Models
{
    /// <summary>
    /// baseline 完整性校验值。算法：把 integritySha256 置空后做紧凑序列化，取 SHA-256。
    /// 放在 Core 里以保证「校验」不依赖任何 Windows API，可离线单测。
    /// </summary>
    public static class SnapshotIntegrity
    {
        public static string Compute(BaselineSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            string original = snapshot.Metadata.IntegritySha256;
            snapshot.Metadata.IntegritySha256 = "";
            string canonical;
            try
            {
                canonical = snapshot.ToJson().ToJson(false);
            }
            finally
            {
                snapshot.Metadata.IntegritySha256 = original;
            }
            return AtomicFile.Sha256OfText(canonical);
        }

        public static bool Matches(BaselineSnapshot snapshot)
        {
            if (snapshot == null) return false;
            string expected = snapshot.Metadata.IntegritySha256 ?? "";
            if (expected.Length == 0) return false;
            return string.Equals(expected, Compute(snapshot), StringComparison.OrdinalIgnoreCase);
        }
    }
}
