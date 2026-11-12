using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using QuietMode.Core.Configuration;
using QuietMode.Core.Json;
using QuietMode.Core.Models;

namespace QuietMode.Core.Tests
{
    /// <summary>
    /// 单测入口。不依赖测试框架（离线可跑，CI 只看退出码）。
    /// Phase 1：JSON 层、默认策略、校验器、原子写 / last-known-good、v1 迁移、state、快照完整性。
    /// Phase 2：磁盘身份解析、安全闸门、操作结果不变式。
    /// </summary>
    public static partial class Program
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new List<string>();

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch { }

            Run("JSON 往返：中文 / 转义 / 嵌套", TestJsonRoundTrip);
            Run("JSON 严格性：拒绝尾逗号与未转义控制字符", TestJsonStrict);
            Run("JSON 错误信息包含行列号", TestJsonErrorPosition);
            Run("默认策略可校验通过", TestDefaultPolicyValid);
            Run("config/default-policy.json 与内嵌默认值一致", TestDefaultPolicyFileMatches);
            Run("校验器：schemaVersion 不符", TestValidatorSchema);
            Run("校验器：时间格式", TestValidatorTime);
            Run("校验器：磁盘无身份", TestValidatorDiskIdentity);
            Run("校验器：磁盘重复身份", TestValidatorDiskDuplicate);
            Run("校验器：风扇开启但路径为空", TestValidatorFanPaths);
            Run("校验器：停转开启但分钟为 0", TestValidatorSpinDown);
            Run("原子写：保留上一版为 backup", TestAtomicBackup);
            Run("加载：损坏配置回退 last-known-good", TestLastKnownGood);
            Run("加载：主备都坏时拒绝", TestBothBroken);
            Run("迁移：v1 INI → v2，磁盘身份解析", TestMigration);
            Run("迁移：无法解析的磁盘被禁用而非回退编号", TestMigrationUnresolvedDisk);
            Run("迁移：已存在 v2 配置时拒绝覆盖", TestMigrationRefuseOverwrite);
            Run("state.json 往返（含恢复进度）", TestStateRoundTrip);
            Run("快照完整性：确定性 / 篡改可检出", TestSnapshotIntegrity);
            Run("基线体检：缺失 / 损坏 / 不完整 / 完整四态", TestBaselineHealth);
            Run("维护任务清单：无重复、含 Regular Maintenance", TestMaintenanceCatalog);
            Run("时间解析边界", TestTimeText);

            RunDiskTests();
            RunMaintenanceTests();
            RunSchedulingTests();
            RunTrayContractTests();

            Console.WriteLine();
            Console.WriteLine("通过 " + _pass + " / 失败 " + _fail);
            foreach (string f in _failures) Console.WriteLine("  FAIL " + f);
            return _fail == 0 ? 0 : 1;
        }

        // ---------------- JSON ----------------

        private static void TestJsonRoundTrip()
        {
            string text = "{\n  \"名称\": \"静音 \\\"Lite\\\"\",\n  \"n\": 20,\n  \"b\": true,\n  \"a\": [1, 2, {\"x\": null}],\n  \"f\": -1.5e3\n}";
            JsonNode node = JsonNode.Parse(text);
            JsonObject o = node.AsObject();
            Eq("静音 \"Lite\"", o.GetString("名称", null));
            Eq(20, o.GetInt("n", 0));
            Eq(true, o.GetBool("b", false));
            Eq(3, o.GetArray("a").Count);
            Eq(-1500d, o.Get("f").AsDouble());

            string roundtrip = node.ToJson(true);
            JsonObject again = JsonNode.Parse(roundtrip).AsObject();
            Eq(o.GetString("名称", null), again.GetString("名称", null));
            Eq(roundtrip, again.ToJson(true));
        }

        private static void TestJsonStrict()
        {
            Throws<JsonParseException>(delegate { JsonNode.Parse("{\"a\":1,}"); });
            Throws<JsonParseException>(delegate { JsonNode.Parse("{\"a\":\"x\ny\"}"); });
            Throws<JsonParseException>(delegate { JsonNode.Parse("{\"a\":1} extra"); });
            Throws<JsonParseException>(delegate { JsonNode.Parse("[1,2"); });
        }

        private static void TestJsonErrorPosition()
        {
            try
            {
                JsonNode.Parse("{\n  \"a\": 1,\n  \"b\" 2\n}");
                Fail("应当抛出解析异常");
            }
            catch (JsonParseException ex)
            {
                True(ex.Message.Contains("第 3 行"), "错误信息应含行号，实际：" + ex.Message);
            }
        }

        // ---------------- 配置 ----------------

        private static void TestDefaultPolicyValid()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(r.IsValid, "默认策略应通过校验：" + r.ToText());
            Eq(QuietModeConfig.CurrentSchemaVersion, cfg.SchemaVersion);
            Eq("23:58", cfg.Schedule.NightStart);
            Eq("10:02", cfg.Schedule.DayStart);
            Eq(20, cfg.SpinDown.Minutes);
        }

        private static void TestDefaultPolicyFileMatches()
        {
            string root = FindRepoRoot();
            if (root == null) { Skip("找不到仓库根目录"); return; }
            string path = Path.Combine(root, "config", "default-policy.json");
            True(File.Exists(path), "缺少 config/default-policy.json");
            string fileText = File.ReadAllText(path, Encoding.UTF8).Trim();
            string embedded = ConfigStore.DefaultPolicyJson.Trim();

            string a = JsonNode.Parse(fileText).ToJson(false);
            string b = JsonNode.Parse(embedded).ToJson(false);
            Eq(b, a);
        }

        private static void TestValidatorSchema()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.SchemaVersion = 1;
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "schemaVersion=1 应被拒绝");
            True(r.Errors[0].Contains("migrate"), "应提示先迁移");
        }

        private static void TestValidatorTime()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.Schedule.NightStart = "24:00";
            cfg.Schedule.DayStart = "10:2";
            cfg.Fan.AutoSwitch.On = "7:5";
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "非法时间应被拒绝");
            Eq(3, r.Errors.Count);
        }

        private static void TestValidatorDiskIdentity()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.Disks.Enabled = true;
            cfg.Disks.Targets.Add(new DiskTarget { LegacyNumber = 1, Enabled = true });
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "无身份磁盘应被拒绝");
            True(r.ToText().Contains("禁止按磁盘编号回退"), "错误信息应说明禁止按编号回退");
        }

        private static void TestValidatorDiskDuplicate()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.Disks.Enabled = true;
            cfg.Disks.Targets.Add(new DiskTarget { UniqueId = "samplehash", Enabled = true });
            cfg.Disks.Targets.Add(new DiskTarget { UniqueId = "samplehash", Enabled = true });
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "重复 uniqueId 应被拒绝");
        }

        private static void TestValidatorFanPaths()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.Fan.Enabled = true;
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "风扇开启但路径为空应被拒绝");
            True(r.Errors.Count >= 4, "应报告 ipcPath / controlExe / normalProfile / silentProfile 四项，实际 " + r.Errors.Count);
        }

        private static void TestValidatorSpinDown()
        {
            QuietModeConfig cfg = ConfigStore.DefaultPolicy();
            cfg.SpinDown.Enabled = true;
            cfg.SpinDown.Minutes = 0;
            ValidationReport r = ConfigValidator.Validate(cfg, false);
            True(!r.IsValid, "停转开启但分钟为 0 应被拒绝");
        }

        // ---------------- 存储 ----------------

        private static void TestAtomicBackup()
        {
            using (TempDir tmp = new TempDir())
            {
                QuietModePaths paths = QuietModePaths.ForRoot(tmp.Path);
                ConfigStore store = new ConfigStore(paths);

                QuietModeConfig v1 = ConfigStore.DefaultPolicy();
                v1.SpinDown.Minutes = 15;
                store.Save(v1, false);

                QuietModeConfig v2 = ConfigStore.DefaultPolicy();
                v2.SpinDown.Minutes = 45;
                store.Save(v2, false);

                True(File.Exists(paths.BackupPath), "应生成 config.backup.json");
                JsonObject backup = JsonNode.Parse(File.ReadAllText(paths.BackupPath, Encoding.UTF8)).AsObject();
                Eq(15, backup.GetObject("spinDown").GetInt("minutes", -1));

                JsonObject current = JsonNode.Parse(File.ReadAllText(paths.ConfigPath, Encoding.UTF8)).AsObject();
                Eq(45, current.GetObject("spinDown").GetInt("minutes", -1));

                True(Directory.GetFiles(tmp.Path, "*.tmp-*").Length == 0, "临时文件应被清理");
            }
        }

        private static void TestLastKnownGood()
        {
            using (TempDir tmp = new TempDir())
            {
                QuietModePaths paths = QuietModePaths.ForRoot(tmp.Path);
                ConfigStore store = new ConfigStore(paths);
                store.Save(ConfigStore.DefaultPolicy(), false);

                QuietModeConfig v2 = ConfigStore.DefaultPolicy();
                v2.SpinDown.Minutes = 33;
                store.Save(v2, false);

                File.WriteAllText(paths.ConfigPath, "{ this is not json", new UTF8Encoding(false));

                ConfigLoadResult r = store.Load(false);
                True(r.Loaded, "应能加载备份");
                True(r.UsedBackup, "应标记为使用了备份");
                Eq(QuietModeConfig.CurrentSchemaVersion, r.Config.SchemaVersion);
            }
        }

        private static void TestBothBroken()
        {
            using (TempDir tmp = new TempDir())
            {
                QuietModePaths paths = QuietModePaths.ForRoot(tmp.Path);
                Directory.CreateDirectory(tmp.Path);
                File.WriteAllText(paths.ConfigPath, "[]", new UTF8Encoding(false));
                ConfigLoadResult r = new ConfigStore(paths).Load(false);
                True(!r.Usable, "根节点不是对象时必须拒绝");
            }
        }

        // ---------------- 迁移 ----------------

        private static void TestMigration()
        {
            using (TempDir tmp = new TempDir())
            {
                string appData = Path.Combine(tmp.Path, "appdata");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "settings.ini"), LegacyIni(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(appData, "state.ini"),
                    "ModeOn=0\nSchedulePaused=1\nAutoSilentActive=0\nAutoSilentSuppressed=1\nAutoOffDayStamp=2026-11-10\n",
                    new UTF8Encoding(false));

                QuietModePaths paths = QuietModePaths.ForRoot(Path.Combine(tmp.Path, "machine"));
                paths.LegacyAppDataDir = appData;

                MigrationResult r = ConfigMigrator.Apply(paths, delegate(int n)
                {
                    return new DiskHint
                    {
                        Number = n,
                        Found = true,
                        UniqueId = "SAMPLE-UNIQUE-0002",
                        SerialNumber = "SAMPLE-SERIAL-0002",
                        FriendlyName = "SAMPLE HDD 4TB",
                        SizeBytes = 4000000000000,
                        BusType = "SATA"
                    };
                }, false);

                True(r.Ok, "迁移应成功：" + r.ToText());
                True(r.WroteConfig && r.WroteState, "应写入 config/state");

                ConfigLoadResult loaded = new ConfigStore(paths).Load(false);
                True(loaded.Usable, "迁移结果应通过校验：" + loaded.Report.ToText());
                Eq(20, loaded.Config.SpinDown.Minutes);
                Eq(true, loaded.Config.SpinDown.Enabled);
                Eq("SAMPLE-UNIQUE-0002", loaded.Config.Disks.Targets[0].UniqueId);
                Eq(1, loaded.Config.Disks.Targets[0].LegacyNumber);
                Eq("override", loaded.Config.Schedule.Mode);
                Eq("C:\\Tools\\FanControl\\Configurations\\FanControl.json", loaded.Config.Fan.NormalProfile);

                QuietModeState state = new StateStore(paths).Load();
                True(state.SchedulePaused, "SchedulePaused 应迁移");
                True(state.AutoSilentSuppressed, "AutoSilentSuppressed 应迁移");
                Eq("2026-11-10", state.AutoOffDayStamp);
            }
        }

        private static void TestMigrationUnresolvedDisk()
        {
            using (TempDir tmp = new TempDir())
            {
                string appData = Path.Combine(tmp.Path, "appdata");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "settings.ini"), LegacyIni(), new UTF8Encoding(false));

                QuietModePaths paths = QuietModePaths.ForRoot(Path.Combine(tmp.Path, "machine"));
                paths.LegacyAppDataDir = appData;

                MigrationResult r = ConfigMigrator.Apply(paths, delegate(int n) { return new DiskHint { Number = n, Found = false }; }, false);
                True(r.Ok, "迁移本身应成功（磁盘目标被丢弃）");
                ConfigLoadResult loaded = new ConfigStore(paths).Load(false);
                True(loaded.Usable, "校验应通过：" + loaded.Report.ToText());
                Eq(0, loaded.Config.Disks.Targets.Count);
                Eq(false, loaded.Config.Disks.Enabled);
                True(r.Warnings.Exists(w => w.Contains("无法解析到稳定身份")), "应有身份解析失败告警");
            }
        }

        private static void TestMigrationRefuseOverwrite()
        {
            using (TempDir tmp = new TempDir())
            {
                string appData = Path.Combine(tmp.Path, "appdata");
                Directory.CreateDirectory(appData);
                File.WriteAllText(Path.Combine(appData, "settings.ini"), LegacyIni(), new UTF8Encoding(false));

                QuietModePaths paths = QuietModePaths.ForRoot(Path.Combine(tmp.Path, "machine"));
                paths.LegacyAppDataDir = appData;
                new ConfigStore(paths).Save(ConfigStore.DefaultPolicy(), false);

                MigrationResult r = ConfigMigrator.Apply(paths, delegate(int n) { return new DiskHint { Found = false }; }, false);
                True(!r.Ok, "已存在 v2 配置时应拒绝覆盖");
                True(r.Errors.Exists(e => e.Contains("--force")), "错误信息应提示 --force");
            }
        }

        // ---------------- state ----------------

        private static void TestStateRoundTrip()
        {
            using (TempDir tmp = new TempDir())
            {
                QuietModePaths paths = QuietModePaths.ForRoot(tmp.Path);
                StateStore store = new StateStore(paths);

                QuietModeState s = new QuietModeState();
                s.ModeOn = true;
                s.AutoSilentActive = true;
                s.AutoOffDayStamp = "2026-11-11";
                s.Recovery.Active = true;
                s.Recovery.Reason = "夜间切换中途失败";
                s.Recovery.Step("RestoreDisks").Status = "failed";
                s.Recovery.Step("RestoreDisks").Detail = "磁盘 #1 仍为脱机";

                store.Save(s);
                QuietModeState back = store.Load();

                True(back.ModeOn, "ModeOn 应保持");
                Eq("2026-11-11", back.AutoOffDayStamp);
                Eq(1, back.Recovery.Steps.Count);
                Eq("failed", back.Recovery.Steps[0].Status);
                Eq("磁盘 #1 仍为脱机", back.Recovery.Steps[0].Detail);
            }
        }

        // ---------------- 快照 ----------------

        private static void TestSnapshotIntegrity()
        {
            BaselineSnapshot s = new BaselineSnapshot();
            s.Metadata.WindowsBuild = "19045.4291";
            s.Metadata.CreatedAtUtc = "2026-11-11T13:00:00Z";
            s.Metadata.ToolVersion = ProductInfo.Version;

            TaskSnapshot t = new TaskSnapshot();
            t.Path = @"\Microsoft\Windows\Defrag\";
            t.Name = "ScheduledDefrag";
            t.Exists = true;
            t.Enabled = false;
            t.XmlSha256 = "samplehash";
            t.OriginalUnknown = true;
            t.OriginalReason = "快照拍摄于夜间窗口内";
            s.ScheduledTasks.Add(t);

            DiskSnapshot d = new DiskSnapshot();
            d.Number = 1;
            d.UniqueId = "SAMPLE-UNIQUE-0002";
            d.SizeBytes = 4000000000000;
            d.IsOffline = true;
            s.Disks.Add(d);

            string first = SnapshotIntegrity.Compute(s);
            s.Metadata.IntegritySha256 = first;
            Eq(first, SnapshotIntegrity.Compute(s));
            True(SnapshotIntegrity.Matches(s), "匹配应成立");

            string json = s.ToJson().ToJson(true);
            BaselineSnapshot back = BaselineSnapshot.FromJson(JsonNode.Parse(json).AsObject());
            Eq(first, SnapshotIntegrity.Compute(back));
            Eq(json, back.ToJson().ToJson(true));

            back.Disks[0].IsOffline = false;
            True(!SnapshotIntegrity.Matches(back), "篡改后应检出");

            // 默认值不落盘：否则一次字段扩展就会让所有历史基线的校验值全部失配
            TaskSnapshot plain = new TaskSnapshot();
            plain.Path = @"\X\";
            plain.Name = "Y";
            True(plain.ToJson().ToJson(false).IndexOf("originalUnknown") < 0, "OriginalUnknown=false 不应写入 JSON");
            plain.OriginalUnknown = true;
            True(plain.ToJson().ToJson(false).IndexOf("originalUnknown") >= 0, "OriginalUnknown=true 必须写入 JSON");
        }

        /// <summary>
        /// 基线体检的四态判断。危险写操作只允许在「完整」时发生，
        /// 损坏的基线必须整份拒用，而不是逐字段「尽量用」。
        /// </summary>
        private static void TestBaselineHealth()
        {
            BaselineHealth missing = BaselineHealth.Evaluate(null, null, null);
            Eq(BaselineState.Missing, missing.State);
            Eq(false, missing.Usable);

            BaselineSnapshot s = new BaselineSnapshot();
            s.Metadata.WindowsBuild = "19045.4291";
            s.Metadata.CreatedAtUtc = "2026-11-12T00:00:00Z";
            s.Metadata.ToolVersion = ProductInfo.Version;
            s.Metadata.SchemaVersion = BaselineSnapshot.CurrentSchemaVersion;
            s.Metadata.Complete = true;

            TaskSnapshot t = new TaskSnapshot();
            t.Path = @"\Microsoft\Windows\Defrag\";
            t.Name = "ScheduledDefrag";
            t.Exists = true;
            t.Enabled = true;
            t.XmlBackup = "tasks/defrag.xml";
            t.XmlSha256 = "samplehash";
            s.ScheduledTasks.Add(t);

            DiskSnapshot disk = new DiskSnapshot();
            disk.Number = 1;
            disk.UniqueId = "SAMPLE-UNIQUE-0002";
            disk.SizeBytes = 4000000000000;
            s.Disks.Add(disk);

            // 没有完整性校验值 ≡ 无法证明没被改过
            BaselineHealth noHash = BaselineHealth.Evaluate(s, delegate { return true; }, delegate { return "samplehash"; });
            Eq(BaselineState.Corrupt, noHash.State);

            s.Metadata.IntegritySha256 = SnapshotIntegrity.Compute(s);
            BaselineHealth ok = BaselineHealth.Evaluate(s, delegate { return true; }, delegate { return "samplehash"; });
            Eq(BaselineState.Complete, ok.State);
            Eq(true, ok.Authoritative);

            BaselineSnapshot tampered = BaselineSnapshot.FromJson(JsonNode.Parse(s.ToJson().ToJson(true)).AsObject());
            tampered.ScheduledTasks[0].Enabled = false;
            Eq(BaselineState.Corrupt, BaselineHealth.Evaluate(tampered, null, null).State);

            BaselineSnapshot noXml = BaselineSnapshot.FromJson(JsonNode.Parse(s.ToJson().ToJson(true)).AsObject());
            BaselineHealth partial = BaselineHealth.Evaluate(noXml, delegate { return false; }, null);
            Eq(BaselineState.Partial, partial.State);
            Eq(true, partial.Usable);
            Eq(false, partial.Authoritative);
            Eq(1, partial.MissingXmlBackups);

            BaselineSnapshot incomplete = BaselineSnapshot.FromJson(JsonNode.Parse(s.ToJson().ToJson(true)).AsObject());
            incomplete.Metadata.Complete = false;
            incomplete.Disks.Clear();
            // 改完要重算校验值，否则测的就不是「不完整」而是「被篡改」
            incomplete.Metadata.IntegritySha256 = SnapshotIntegrity.Compute(incomplete);
            Eq(BaselineState.Partial, BaselineHealth.Evaluate(incomplete, delegate { return true; }, delegate { return "samplehash"; }).State);

            // 比本版本更新的 schema 读不懂 → 一个字节都不能用
            BaselineSnapshot futureSchema = BaselineSnapshot.FromJson(JsonNode.Parse(s.ToJson().ToJson(true)).AsObject());
            futureSchema.SchemaVersion = BaselineSnapshot.CurrentSchemaVersion + 1;
            Eq(BaselineState.Corrupt, BaselineHealth.Evaluate(futureSchema, delegate { return true; }, delegate { return "samplehash"; }).State);

            // 旧 schema 必须能读：升级不能把用户已有的基线判成损坏
            BaselineSnapshot oldSchema = BaselineSnapshot.FromJson(JsonNode.Parse(s.ToJson().ToJson(true)).AsObject());
            oldSchema.SchemaVersion = 2;
            oldSchema.Metadata.IntegritySha256 = SnapshotIntegrity.Compute(oldSchema);
            BaselineHealth oldHealth = BaselineHealth.Evaluate(oldSchema, delegate { return true; }, delegate { return "samplehash"; });
            Eq(BaselineState.Partial, oldHealth.State);
            Eq(true, oldHealth.Usable);
            Eq(false, oldHealth.Authoritative);
        }

        private static void TestMaintenanceCatalog()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MaintenanceTaskDefinition t in MaintenanceCatalog.All)
            {
                True(seen.Add(t.FullName), "维护任务重复：" + t.FullName);
                True(!string.IsNullOrEmpty(t.Purpose), "任务缺少说明：" + t.FullName);
            }
            True(seen.Contains(@"\Microsoft\Windows\TaskScheduler\Regular Maintenance"), "应含 Regular Maintenance");
            True(MaintenanceCatalog.GateTasks.Count == 2, "闸门任务应为 2 个");
            True(MaintenanceCatalog.NightDisableTargets.Exists(t => t.Name == "ScheduledDefrag"), "ScheduledDefrag 应在夜间停用清单");
        }

        private static void TestTimeText()
        {
            int h, m;
            True(TimeText.TryParseHhmm("23:58", out h, out m) && h == 23 && m == 58, "23:58");
            True(TimeText.TryParseHhmm("0:05", out h, out m) && h == 0 && m == 5, "0:05");
            True(!TimeText.TryParseHhmm("24:00", out h, out m), "24:00 应失败");
            True(!TimeText.TryParseHhmm("10:60", out h, out m), "10:60 应失败");
            True(!TimeText.TryParseHhmm("", out h, out m), "空串应失败");
            True(!TimeText.TryParseHhmm("samplehash", out h, out m), "非数字应失败");
            Eq(1438, TimeText.ToMinutes("23:58"));
        }

        // ---------------- harness ----------------

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _pass++;
                Console.WriteLine("PASS  " + name);
            }
            catch (Exception ex)
            {
                _fail++;
                _failures.Add(name + " — " + ex.Message);
                Console.WriteLine("FAIL  " + name + " — " + ex.Message);
            }
        }

        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("期望 <" + expected + ">，实际 <" + actual + ">");
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void Fail(string message) { throw new Exception(message); }

        private static void Throws<T>(Action action) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new Exception("期望抛出 " + typeof(T).Name);
        }

        private static void Skip(string reason) { Console.WriteLine("SKIP  " + reason); }

        private static string FindRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "README.md"))
                    && Directory.Exists(Path.Combine(dir.FullName, "config")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static string LegacyIni()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("# v1 settings");
            b.AppendLine("FanEnabled=1");
            b.AppendLine("FanIpcCtl=C:\\Tools\\FanControl\\ipc\\IpcCtl.exe");
            b.AppendLine("FanNormal=C:\\Tools\\FanControl\\Configurations\\FanControl.json");
            b.AppendLine("FanSilent=C:\\Tools\\FanControl\\Configurations\\FanControl_Silent_Lite.json");
            b.AppendLine("FanControlExe=C:\\Tools\\FanControl\\FanControl.exe");
            b.AppendLine("FanFlag=C:\\Tools\\FanControl\\silent_active.flag");
            b.AppendLine("HddEnabled=1");
            b.AppendLine("HddDisks=1");
            b.AppendLine("HddForce=0");
            b.AppendLine("MaintenanceEnabled=1");
            b.AppendLine("SpinDownEnabled=1");
            b.AppendLine("SpinDownMinutes=20");
            b.AppendLine("ScheduleMode=override");
            b.AppendLine("GateEnabled=1");
            b.AppendLine("AutoSilent=1");
            b.AppendLine("AutoSilentOn=23:58");
            b.AppendLine("AutoSilentOff=10:02");
            b.AppendLine("AutoStart=1");
            b.AppendLine("RestoreOnBoot=0");
            return b.ToString();
        }

        private sealed class TempDir : IDisposable
        {
            public readonly string Path;

            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qmtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Path);
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); }
                catch { }
            }
        }
    }
}
