using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuietMode.Core.Json;
using QuietMode.Core.Models;

namespace QuietMode.Core.Configuration
{
    public sealed class ConfigLoadResult
    {
        public QuietModeConfig Config;
        public ValidationReport Report = new ValidationReport();
        public bool Loaded;
        public bool UsedBackup;
        public string SourcePath = "";
        public string Error = "";

        public bool Usable { get { return Loaded && Report != null && Report.IsValid; } }
    }

    /// <summary>
    /// config.json 读写。加载顺序：config.json → config.backup.json（last-known-good）→ 失败。
    /// 写入前强制校验，写入后复读校验，保证磁盘上永远是可用配置。
    /// </summary>
    public sealed class ConfigStore
    {
        public readonly QuietModePaths Paths;

        public ConfigStore(QuietModePaths paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            Paths = paths;
        }

        public ConfigLoadResult Load(bool checkFileSystem)
        {
            ConfigLoadResult result = new ConfigLoadResult();

            if (!File.Exists(Paths.ConfigPath))
            {
                if (File.Exists(Paths.BackupPath))
                {
                    result = LoadOne(Paths.BackupPath, checkFileSystem);
                    result.UsedBackup = result.Loaded;
                    if (result.Loaded) result.Report.Warn("config.json 缺失，已回退到 config.backup.json");
                    return result;
                }
                result.Error = "未找到 config.json（" + Paths.ConfigPath + "）";
                return result;
            }

            result = LoadOne(Paths.ConfigPath, checkFileSystem);
            if (result.Usable) return result;

            // 主配置损坏 → 尝试 last-known-good
            if (File.Exists(Paths.BackupPath))
            {
                ConfigLoadResult backup = LoadOne(Paths.BackupPath, checkFileSystem);
                if (backup.Usable)
                {
                    backup.UsedBackup = true;
                    backup.Report.Warn("config.json 校验失败，已加载 config.backup.json");
                    foreach (string e in result.Report.Errors) backup.Report.Warn("原错误：" + e);
                    return backup;
                }
            }
            return result;
        }

        private ConfigLoadResult LoadOne(string path, bool checkFileSystem)
        {
            ConfigLoadResult result = new ConfigLoadResult();
            result.SourcePath = path;
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                JsonNode node;
                string parseError;
                if (!JsonNode.TryParse(text, out node, out parseError))
                {
                    result.Error = path + "：" + parseError;
                    result.Report.Error(result.Error);
                    return result;
                }

                JsonObject root = node.TryObject();
                if (root == null)
                {
                    result.Error = path + "：根节点不是对象";
                    result.Report.Error(result.Error);
                    return result;
                }

                QuietModeConfig cfg = QuietModeConfig.FromJson(root);
                result.Config = cfg;
                result.Report = ConfigValidator.Validate(cfg, checkFileSystem);
                result.Loaded = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = path + "：" + ex.Message;
                result.Report.Error(result.Error);
                return result;
            }
        }

        /// <summary>校验通过才写；失败抛 ConfigValidationException，磁盘上的旧文件保持不动。</summary>
        public void Save(QuietModeConfig cfg, bool checkFileSystem)
        {
            ValidationReport report = ConfigValidator.Validate(cfg, false);
            if (!report.IsValid)
            {
                throw new ConfigValidationException(report);
            }
            if (checkFileSystem)
            {
                ValidationReport fs = ConfigValidator.Validate(cfg, true);
                if (!fs.IsValid) throw new ConfigValidationException(fs);
            }

            string text = cfg.ToJsonText() + "\n";
            AtomicFile.WriteText(Paths.ConfigPath, text, Paths.BackupPath, delegate(string reread)
            {
                JsonNode node;
                string err;
                if (!JsonNode.TryParse(reread, out node, out err))
                    throw new InvalidDataException("写入后复读失败：" + err);
                QuietModeConfig again = QuietModeConfig.FromJson(node.AsObject());
                ValidationReport r2 = ConfigValidator.Validate(again, false);
                if (!r2.IsValid) throw new ConfigValidationException(r2);
            });
        }

        /// <summary>不存在则落默认策略；已存在不动。</summary>
        public QuietModeConfig EnsureDefault()
        {
            if (File.Exists(Paths.ConfigPath))
            {
                ConfigLoadResult existing = Load(false);
                if (existing.Usable) return existing.Config;
            }
            QuietModeConfig cfg = DefaultPolicy();
            Save(cfg, false);
            return cfg;
        }

        public static QuietModeConfig DefaultPolicy()
        {
            return QuietModeConfig.FromJson(JsonNode.Parse(DefaultPolicyJson).AsObject());
        }

        /// <summary>config/default-policy.json 的同一份内容（测试会比对两者一致）。</summary>
        public const string DefaultPolicyJson = @"{
  ""schemaVersion"": 2,
  ""schedule"": {
    ""enabled"": true,
    ""mode"": ""override"",
    ""nightStart"": ""23:58"",
    ""dayStart"": ""10:02"",
    ""startupOverrideEnabled"": true,
    ""startupDayStart"": ""06:00"",
    ""startupGraceMinutes"": 15,
    ""reconcileIntervalMinutes"": 10
  },
  ""fan"": {
    ""enabled"": false,
    ""ipcPath"": """",
    ""controlExe"": """",
    ""normalProfile"": """",
    ""silentProfile"": """",
    ""flagPath"": """",
    ""autoSwitch"": {
      ""enabled"": true,
      ""on"": ""23:58"",
      ""off"": ""10:02""
    }
  },
  ""disks"": {
    ""enabled"": false,
    ""forceBusy"": false,
    ""targets"": []
  },
  ""maintenance"": {
    ""enabled"": true,
    ""strictNightDisable"": true,
    ""disableSysMainAtNight"": true,
    ""maintenanceHour"": 10,
    ""disableWake"": true
  },
  ""spinDown"": {
    ""enabled"": false,
    ""minutes"": 20
  },
  ""startup"": {
    ""autoStart"": false,
    ""restoreOnBoot"": false
  }
}";
    }

    public sealed class ConfigValidationException : Exception
    {
        public readonly ValidationReport Report;
        public ConfigValidationException(ValidationReport report)
            : base(report == null ? "配置校验失败" : report.ToText())
        {
            Report = report;
        }
    }

    /// <summary>state.json 读写（运行状态 + 恢复进度）。</summary>
    public sealed class StateStore
    {
        public readonly QuietModePaths Paths;

        public StateStore(QuietModePaths paths) { Paths = paths; }

        public QuietModeState Load()
        {
            try
            {
                if (!File.Exists(Paths.StatePath)) return new QuietModeState();
                string text = File.ReadAllText(Paths.StatePath, Encoding.UTF8);
                JsonNode node;
                string err;
                if (!JsonNode.TryParse(text, out node, out err)) return new QuietModeState();
                JsonObject root = node.TryObject();
                if (root == null) return new QuietModeState();
                return QuietModeState.FromJson(root);
            }
            catch
            {
                return new QuietModeState();
            }
        }

        public void Save(QuietModeState state)
        {
            if (state == null) throw new ArgumentNullException("state");
            string text = state.ToJson().ToJson(true) + "\n";
            AtomicFile.WriteText(Paths.StatePath, text, null, null);
        }
    }
}
