using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-17a (ADR-69) networkTargets in nexus_config.json + /reload ─────────────────────────────

        /// <summary>
        /// TASK-17a "net-targets" (a temp nexus_config.json only: the user's file is never touched; pings to loopback / TEST-NET):
        ///   E2  custom networkTargets (LOOPBACK-TEST + NITRO5-LAN on 127.0.0.1) applied by Reload(), the scan uses them at once,
        ///       LOOPBACK-TEST ONLINE, NITRO5 line from the NITRO5* entry; at most 8 entries kept
        ///   E3  missing field / [] / {"label":"","host":null} / whole file broken → the 4 defaults (Gateway .1, NITRO5-LAN .22,
        ///       NITRO5-WIFI .192, LAN-TV .97), warnings only; a mixed list keeps its valid entries; no NITRO5 entry → line says so;
        ///       the default config file is written without a "networkTargets" field
        /// </summary>
        private static (object Report, bool Pass) NetTargets(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            string temp = Path.Combine(Path.GetTempPath(), "nexus_task17a_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            string path = Path.Combine(temp, NexusConfigService.FileName);
            var steps = new List<object>();
            try
            {
                var config = new NexusConfigService(path);
                var radar = new NetworkRadar
                {
                    TargetsProvider = () => config.Current.NetworkTargets?.Select(t => new NetworkRadar.Target(t.Label, t.Host)).ToList()
                };
                string baseText = File.ReadAllText(path, Encoding.UTF8);
                checks["e3.defaultFileHasNoField"] = !baseText.Contains("networkTargets", StringComparison.OrdinalIgnoreCase);

                NexusConfigLoadResult Write(JsonNode? targets, string? raw = null)
                {
                    string text = raw ?? Patch(baseText, targets);
                    File.WriteAllText(path, text, new UTF8Encoding(false));
                    return config.Reload();
                }

                bool IsDefault() =>
                    radar.CurrentTargets(out string src) is var t && src == "default" && t.Count == 4 &&
                    t[0] is { Label: "Gateway", Host: "192.168.1.1" } && t[1] is { Label: "NITRO5-LAN", Host: "192.168.1.22" } &&
                    t[2] is { Label: "NITRO5-WIFI", Host: "192.168.1.192" } && t[3] is { Label: "LAN-TV", Host: "192.168.1.97" };

                // E2: custom list + Reload, scan at once
                NexusConfigLoadResult custom = Write(new JsonArray
                {
                    new JsonObject { ["label"] = "LOOPBACK-TEST", ["host"] = "127.0.0.1" },
                    new JsonObject { ["label"] = "NITRO5-LAN", ["host"] = "127.0.0.1" },
                    new JsonObject { ["label"] = "TEST-NET", ["host"] = "192.0.2.1" }
                });
                NetRadarSnapshot scan = radar.ScanAsync().GetAwaiter().GetResult();
                NetPingSample? loop = scan.Pings.FirstOrDefault(p => p.Label == "LOOPBACK-TEST");
                checks["e2.customApplied"] = custom.Ok && scan.TargetsSource == "config" && scan.Pings.Count == 3 && loop is { Ok: true };
                checks["e2.nitro5FromLabel"] = scan.Nitro5State.StartsWith("ONLINE", StringComparison.Ordinal) && scan.Nitro5Address == "127.0.0.1" &&
                                               NetworkRadar.Nitro5Line(scan).StartsWith("⚡ NITRO5", StringComparison.Ordinal);
                checks["e2.pingsCut"] = scan.PingMs <= 300 && scan.Pings.All(p => p.ElapsedMs <= 300);
                checks["e2.cardShowsTarget"] = NetworkRadar.Describe(scan).Contains("LOOPBACK-TEST", StringComparison.Ordinal) &&
                                               NetworkRadar.Describe(scan).Contains("nexus_config.json", StringComparison.Ordinal);
                steps.Add(new { step = "custom", custom.Ok, warnings = custom.Warnings, scan.TargetsSource, pings = scan.Pings, scan.Nitro5State, scan.PingMs });

                var ten = new JsonArray();
                for (int i = 1; i <= 10; i++) ten.Add(new JsonObject { ["label"] = $"T{i}", ["host"] = "127.0.0.1" });
                NexusConfigLoadResult tenLoad = Write(ten);
                IReadOnlyList<NetworkRadar.Target> eight = radar.CurrentTargets(out string tenSrc);
                checks["e2.maxEight"] = tenSrc == "config" && eight.Count == NexusConfigService.MaxNetworkTargets && eight[7].Label == "T8" &&
                                        tenLoad.Warnings.Any(w => w.Contains("networkTargets", StringComparison.Ordinal));
                steps.Add(new { step = "ten", count = eight.Count, warnings = tenLoad.Warnings });

                // E3: fallbacks
                NexusConfigLoadResult missing = Write(null);
                checks["e3.missing"] = missing.Ok && IsDefault();
                NexusConfigLoadResult empty = Write(new JsonArray());
                checks["e3.empty"] = empty.Ok && IsDefault();
                NexusConfigLoadResult broken = Write(new JsonArray { new JsonObject { ["label"] = "", ["host"] = null } });
                checks["e3.brokenItem"] = broken.Ok && IsDefault() && broken.Warnings.Any(w => w.Contains("networkTargets", StringComparison.Ordinal));
                NexusConfigLoadResult notArray = Write(JsonValue.Create("192.168.1.1"));
                checks["e3.notArray"] = notArray.Ok && IsDefault();
                NexusConfigLoadResult mixed = Write(new JsonArray
                {
                    new JsonObject { ["label"] = "   ", ["host"] = "10.0.0.1" },
                    new JsonObject { ["label"] = "ONLY-ONE", ["host"] = "127.0.0.1" },
                    new JsonObject { ["host"] = "127.0.0.2" }
                });
                IReadOnlyList<NetworkRadar.Target> mixedTargets = radar.CurrentTargets(out string mixedSrc);
                checks["e3.mixedKeepsValid"] = mixed.Ok && mixedSrc == "config" && mixedTargets.Count == 1 && mixedTargets[0].Label == "ONLY-ONE";
                NetRadarSnapshot noNitro = radar.ScanAsync().GetAwaiter().GetResult();
                checks["e3.noNitroEntry"] = noNitro.Nitro5State == NetworkRadar.Nitro5NotConfigured &&
                                            NetworkRadar.Nitro5Line(noNitro).Contains("không có trong networkTargets", StringComparison.Ordinal);
                // custom first, then a whole broken file: the last good settings stay, networkTargets goes back to the defaults
                Write(new JsonArray { new JsonObject { ["label"] = "LOOPBACK-TEST", ["host"] = "127.0.0.1" } });
                NexusConfigLoadResult wholeBroken = Write(null, "{ \"networkTargets\": [ { \"label\": \"X\", ");
                string rewritten = File.ReadAllText(path, Encoding.UTF8);
                checks["e3.brokenFile"] = !wholeBroken.Ok && wholeBroken.Restored && IsDefault() && !rewritten.Contains("networkTargets", StringComparison.OrdinalIgnoreCase);
                steps.Add(new
                {
                    step = "fallbacks",
                    missing = missing.Warnings, empty = empty.Warnings, broken = broken.Warnings, notArray = notArray.Warnings,
                    mixed = mixed.Warnings, mixedCount = mixedTargets.Count, noNitro = noNitro.Nitro5State, wholeBroken = new { wholeBroken.Ok, wholeBroken.Restored, wholeBroken.Error }
                });
                checks["e3.describe"] = config.Describe().Contains("networkTargets=4 đích mặc định", StringComparison.Ordinal);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            // the runtime of this process: what /net would use with the user's nexus_config.json (report only)
            IReadOnlyList<NetworkRadar.Target> live = runtime.Network.CurrentTargets(out string liveSource);
            bool pass = checks.Values.All(v => v);
            return (new
            {
                scenario = "net-targets",
                pass,
                failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                checks,
                steps,
                thisMachine = new { source = liveSource, targets = live }
            }, pass);
        }

        /// <summary>The default config text with "networkTargets" set (null = field removed).</summary>
        private static string Patch(string baseText, JsonNode? targets)
        {
            JsonObject root = JsonNode.Parse(baseText)!.AsObject();
            root.Remove("networkTargets");
            if (targets != null) root["networkTargets"] = targets;
            return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
    }
}
