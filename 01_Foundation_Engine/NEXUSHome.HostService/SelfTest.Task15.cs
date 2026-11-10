using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-15 (ADR-62) /ctx + Anti-Cheat Safe Guard + Sensitive Window Denylist ─────────────────────────────

        private const double CtxBlockP95MaxMs = 0.5;   // spec E2
        private const double CtxCacheHitP95MaxMs = 3.0; // spec E3
        private const double CtxInspectP95MaxMs = 2.0;  // spec E3 (MCP structure; the McpTools process is timed live by Verify-Task15)

        /// <summary>
        /// TASK-15 "ctx-safeguard" (simulated windows only, mock Gemini, temp nothing else; every dispatch row is is_test = 1):
        ///   E1  name / keyword rules and their edge cases (variants, short words standing alone, fail-closed names)
        ///   E2  10 anti-cheat + 10 sensitive samples: 100 % blocked, P95 &lt;= 0,5 ms, no title / no token anywhere (latest_dispatch.json,
        ///       the SQLite file and its WAL are searched for a unique marker)
        ///   E3  /ctx status and /ctx &lt;question&gt; on a normal window: app / title / block &lt;= 1 200 chars, 25 repeats = cache hits
        ///       P95 &lt;= 3,0 ms and 0 tokens, a different window = cache miss
        ///   E4  the Q&amp;A history (GetAnswerHistory) holds none of the /ctx test rows
        /// </summary>
        private static (object Report, bool Pass) CtxSafeguard(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            string marker = "ctx" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string? oldMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            if (string.IsNullOrEmpty(oldMock)) Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, "Gate 15 ctx mock");
            try
            {
                runtime.QuotaGuard.ResetSlidingWindows();
                object rules = CtxRules(checks);
                object blocks = CtxBlocks(runtime, marker, checks);
                object flow = CtxFlow(runtime, marker, checks);
                bool pass = checks.Values.All(v => v);
                return (new { scenario = "ctx-safeguard", pass, failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(), checks, rules, blocks, flow }, pass);
            }
            finally
            {
                ActiveWindowContext.SimulatedTarget = null;
                ActiveWindowContext.SimulatedRunning = null;
                ActiveWindowContext.InvalidateScan();
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, oldMock);
            }
        }

        private static object CtxRules(Dictionary<string, bool> checks)
        {
            string[] blockedNames = { "vgc", "VGC.exe", "vgk", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEService_x64", "BattlEye",
                                      "FACEIT", "faceitclient", "cs2", "Valorant-Win64-Shipping", "valorant", "League of Legends", "LeagueClient",
                                      "GenshinImpact", "StarRail", "r5apex", "PUBG-Win64-Shipping", "FortniteClient-Win64-Shipping", "xhunter1", "GameGuard", "Ricochet" };
            string?[] allowedNames = { "cs2tool", "vgcx", "notepad", "Code", "pwsh", "WindowsTerminal", "chrome", "", null };
            var nameMisses = blockedNames.Where(n => !ActiveWindowContext.IsAntiCheatName(n)).Concat(allowedNames.Where(ActiveWindowContext.IsAntiCheatName).Select(n => n ?? "(null)")).ToArray();
            checks["e1.antiCheatNames"] = nameMisses.Length == 0;

            var sensitive = new (string? Process, string Title, string Category)[]
            {
                ("Bitwarden", "Bitwarden", "password-manager"), ("KeePassXC", "Database", "password-manager"),
                ("msedge", "Trang mới - InPrivate - Microsoft Edge", "private-browsing"), ("chrome", "New Tab - Google Chrome (Incognito)", "private-browsing"),
                ("Notepad", ".env - Notepad", "secret-file"), ("Code", "id_ed25519 - ssh", "secret-file"),
                ("msedge", "Vietcombank OTP", "one-time-code"), ("chrome", "Xác thực 2FA", "one-time-code"),
                ("chrome", "Đổi mật khẩu", "secret"), ("chrome", "Internet Banking", "finance"), ("chrome", "MetaMask", "crypto-wallet")
            };
            var allowedTitles = new (string? Process, string Title)[]
            {
                ("chrome", "Hotpot recipes - Google Chrome"), ("Code", "Photoshop scripting"), ("chrome", "2fast 2furious"),
                ("Code", "ActiveWindowContext.cs - NEXUS - Visual Studio Code"), ("WindowsTerminal", "Windows PowerShell")
            };
            var keywordMisses = sensitive.Where(x => ActiveWindowContext.MatchSensitive(x.Process, x.Title) != x.Category).Select(x => x.Title)
                .Concat(allowedTitles.Where(x => ActiveWindowContext.MatchSensitive(x.Process, x.Title) != null).Select(x => x.Title)).ToArray();
            checks["e1.denylistRules"] = keywordMisses.Length == 0;

            string longTitle = new string('t', 500);
            string block = ActiveWindowContext.BuildBlock("Code", longTitle.Substring(0, ActiveWindowContext.MaxTitleChars), new string('c', 5000));
            string fpA = ActiveWindowContext.Fingerprint("Code", "a", null), fpB = ActiveWindowContext.Fingerprint("Code", "b", null);
            checks["e1.blockAndFingerprint"] = block.Length <= ActiveWindowContext.MaxBlockChars && block.Contains("| text=", StringComparison.Ordinal) &&
                                               block.EndsWith("]", StringComparison.Ordinal) && fpA != fpB && fpA == ActiveWindowContext.Fingerprint("Code", "a", null);
            return new { nameMisses, keywordMisses, blockChars = block.Length };
        }

        private static object CtxBlocks(NexusRuntime runtime, string marker, Dictionary<string, bool> checks)
        {
            string[] antiCheat = { "vgc|Riot Vanguard", "EasyAntiCheat|EasyAntiCheat", "cs2|Counter-Strike 2", "valorant|VALORANT" };
            string[] sensitive = { $"Bitwarden|Bitwarden {marker}", $"msedge|InPrivate {marker} - Microsoft Edge", $"Notepad|.env {marker} - Notepad", $"msedge|Vietcombank OTP {marker}" };
            ActiveWindowContext.SimulatedRunning = Array.Empty<string>(); // isolate the denylist from whatever runs on this machine
            foreach (string w in antiCheat.Concat(sensitive)) { ActiveWindowContext.SimulatedTarget = w; ActiveWindowContext.Inspect(IntPtr.Zero); } // JIT warm-up

            List<CtxResult> Run(string[] set)
            {
                var results = new List<CtxResult>();
                for (int i = 0; i < 10; i++)
                {
                    ActiveWindowContext.SimulatedTarget = set[i % set.Length];
                    results.Add(ActiveWindowContext.Inspect(IntPtr.Zero, () => "không được đọc"));
                }
                return results;
            }
            List<CtxResult> ac = Run(antiCheat), sens = Run(sensitive);
            double P95(IEnumerable<CtxResult> r) { var t = r.Select(x => x.GuardMs).OrderBy(x => x).ToList(); return Math.Round(t[(int)Math.Ceiling(t.Count * 0.95) - 1], 4); }
            double acP95 = P95(ac), sensP95 = P95(sens);
            checks["e2.antiCheatBlocked"] = ac.All(r => r.Status == ActiveWindowContext.StatusAntiCheat && r.WindowTitle == null && r.ClipText == null && r.Shield == "anticheat-foreground");
            checks["e2.sensitiveBlocked"] = sens.All(r => r.Status == ActiveWindowContext.StatusSensitive && r.WindowTitle == null && r.ProcessName == null &&
                                                          r.ClipText == null && !r.Message.Contains(marker, StringComparison.Ordinal));
            checks["e2.blockP95"] = WithinSla("ctx anti-cheat block P95", acP95, CtxBlockP95MaxMs) & WithinSla("ctx sensitive block P95", sensP95, CtxBlockP95MaxMs);

            // anti-cheat RUNNING (not in front): blocked before the title is read
            ActiveWindowContext.SimulatedRunning = new[] { "notepad", "vgc" };
            ActiveWindowContext.SimulatedTarget = $"Notepad|ghi chú {marker}";
            bool titleRead = false;
            CtxResult running = ActiveWindowContext.Inspect(IntPtr.Zero, () => { titleRead = true; return "x"; });
            checks["e2.runningAntiCheat"] = running.Status == ActiveWindowContext.StatusAntiCheat && running.Shield == "anticheat-running" &&
                                           running.MatchedRule == "vgc" && running.WindowTitle == null && !titleRead;

            // the real Toolhelp32 scan of this machine (reported; the 2 s reuse is what keeps /ctx fast)
            ActiveWindowContext.SimulatedRunning = null;
            ActiveWindowContext.InvalidateScan();
            var cold = ActiveWindowContext.RunningAntiCheat();
            var warm = ActiveWindowContext.RunningAntiCheat();
            ActiveWindowContext.SimulatedRunning = Array.Empty<string>();

            // leak proof through the real command path: status + question on each sensitive window, then search every output
            TokenLedger before = TokenLedger.Read(runtime);
            var processor = new CockpitCommandProcessor(runtime);
            var messages = new List<string>();
            foreach (string w in sensitive)
            {
                ActiveWindowContext.SimulatedTarget = w;
                messages.Add(processor.Execute("/ctx status", IntPtr.Zero).Message);
                CockpitCommandResult q = processor.Execute("/ctx --no-clip đây là gì?", IntPtr.Zero);
                messages.Add(q.Message);
                checks[$"e2.noDispatch.{w.Split('|')[0]}"] = q.Dispatch == null && q.AiTask == null && q.Ctx?.Status == ActiveWindowContext.StatusSensitive;
            }
            foreach (string w in antiCheat)
            {
                ActiveWindowContext.SimulatedTarget = w;
                CockpitCommandResult q = processor.Execute("/ctx --no-clip đây là gì?", IntPtr.Zero);
                messages.Add(q.Message);
                checks[$"e2.noDispatch.{w.Split('|')[0]}"] = q.Dispatch == null && q.AiTask == null && q.Ctx?.Status == ActiveWindowContext.StatusAntiCheat;
            }
            TokenLedger after = TokenLedger.Read(runtime);
            string latest = File.Exists(runtime.LatestDispatchPath) ? File.ReadAllText(runtime.LatestDispatchPath, Encoding.UTF8) : string.Empty;
            bool inDb = FileContains(runtime.DatabasePath, marker) || FileContains(runtime.DatabasePath + "-wal", marker);
            checks["e2.noLeak"] = !messages.Any(m => m.Contains(marker, StringComparison.Ordinal)) && !latest.Contains(marker, StringComparison.Ordinal) && !inDb;
            checks["e2.zeroTokens"] = before.SameTokens(after) && before.DispatchRows == after.DispatchRows;
            return new
            {
                antiCheat = ac.Select(r => new { r.MatchedRule, r.Shield, r.GuardMs }),
                sensitive = sens.Select(r => new { r.MatchedRule, r.Shield, r.GuardMs }),
                acP95, sensP95, slaMs = CtxBlockP95MaxMs,
                running = new { running.Status, running.Shield, running.MatchedRule, titleRead },
                realScan = new { coldMatches = cold.Matches, coldMs = cold.ScanMs, warmCached = warm.Cached },
                latestDispatchHasMarker = latest.Contains(marker, StringComparison.Ordinal), databaseHasMarker = inDb,
                tokensBefore = before, tokensAfter = after
            };
        }

        private static object CtxFlow(NexusRuntime runtime, string marker, Dictionary<string, bool> checks)
        {
            ActiveWindowContext.SimulatedRunning = Array.Empty<string>();
            string title = $"ActiveWindowContext.cs - NEXUS {marker} - Visual Studio Code";
            ActiveWindowContext.SimulatedTarget = $"Code|{title}";
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult status = processor.Execute("/ctx status", IntPtr.Zero);
            checks["e3.status"] = status.Ok && status.Ctx?.Status == ActiveWindowContext.StatusOk && status.Ctx.ProcessName == "Code" &&
                                  status.Ctx.WindowTitle == title && status.Ctx.ContextBlock!.Length <= ActiveWindowContext.MaxBlockChars &&
                                  status.Dispatch == null && status.AiTask == null;

            // first question: mock Gemini answers; then 25 repeats on the same window = cache hits
            string question = "Đoạn này làm gì? " + marker;
            CockpitCommandResult first = processor.Execute($"/ctx --no-clip {question}", IntPtr.Zero);
            ConsultOutcome? firstOutcome = first.AiTask?.GetAwaiter().GetResult();
            TokenLedger beforeHits = TokenLedger.Read(runtime);
            var hitTimes = new List<double>();
            int hits = 0;
            for (int i = 0; i < 25; i++)
            {
                CockpitCommandResult r = processor.Execute($"/ctx --no-clip {question}", IntPtr.Zero);
                hitTimes.Add(r.ElapsedMs);
                if (r.CacheHit) hits++;
            }
            TokenLedger afterHits = TokenLedger.Read(runtime);
            hitTimes.Sort();
            double hitP50 = Math.Round(hitTimes[hitTimes.Count / 2], 3), hitP95 = Math.Round(hitTimes[(int)Math.Ceiling(hitTimes.Count * 0.95) - 1], 3);
            checks["e3.firstAnswer"] = first.Ok && first.Kind == "ctx" && first.Dispatch != null && firstOutcome?.AiOk == true;
            checks["e3.cacheHits"] = hits == 25 && beforeHits.SameTokens(afterHits);
            checks["e3.cacheHitP95"] = WithinSla("ctx cache hit P95", hitP95, CtxCacheHitP95MaxMs);

            // another window = another fingerprint = no stale hit
            ActiveWindowContext.SimulatedTarget = $"Code|Other.cs - NEXUS {marker} - Visual Studio Code";
            CockpitCommandResult other = processor.Execute($"/ctx --no-clip {question}", IntPtr.Zero);
            other.AiTask?.GetAwaiter().GetResult();
            checks["e3.otherWindowMiss"] = !other.CacheHit && other.Dispatch != null;

            // the structure MCP returns, warm inspect timing
            ActiveWindowContext.SimulatedTarget = $"Code|{title}";
            var inspectTimes = new List<double>();
            CtxResult last = ActiveWindowContext.Inspect(IntPtr.Zero);
            for (int i = 0; i < 10; i++) { last = ActiveWindowContext.Inspect(IntPtr.Zero); inspectTimes.Add(last.TotalMs); }
            inspectTimes.Sort();
            double inspectP95 = Math.Round(inspectTimes[(int)Math.Ceiling(inspectTimes.Count * 0.95) - 1], 4);
            checks["e3.inspectShape"] = last.Status == "OK" && last.ProcessName == "Code" && last.ContextBlock!.StartsWith("[ACTIVE_WINDOW_CONTEXT: app=Code | title=", StringComparison.Ordinal) &&
                                        last.Fingerprint!.StartsWith("CTX_V1:", StringComparison.Ordinal);
            checks["e3.inspectP95"] = WithinSla("ctx inspect P95", inspectP95, CtxInspectP95MaxMs);

            // E4: none of these is_test rows reaches the Q&A history of the Cockpit
            long[] testIds = new[] { first.Dispatch?.DispatchId ?? -1, other.Dispatch?.DispatchId ?? -1 };
            RecentDispatch? firstRow = first.Dispatch == null ? null : runtime.Store.GetDispatch(first.Dispatch.DispatchId);
            IReadOnlyList<RecentDispatch> history = runtime.Store.GetAnswerHistory(30);
            checks["e4.historyClean"] = testIds.All(id => id > 0) && history.All(r => !testIds.Contains(r.Id)) && firstRow != null;
            return new
            {
                status = status.Message,
                firstDispatch = first.Dispatch?.DispatchId,
                firstModel = firstOutcome?.Snapshot?.AiModel,
                hits, hitP50, hitP95, slaMs = CtxCacheHitP95MaxMs,
                otherCacheHit = other.CacheHit,
                inspectP95, inspectSlaMs = CtxInspectP95MaxMs,
                testIds, historyIds = history.Select(r => r.Id).ToArray()
            };
        }

        /// <summary>True when <paramref name="text"/> (UTF-8) is anywhere in the file (read shared, so a live SQLite file can be searched).</summary>
        private static bool FileContains(string path, string text)
        {
            if (!File.Exists(path)) return false;
            byte[] needle = Encoding.UTF8.GetBytes(text);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] data = new byte[fs.Length];
            int read = 0;
            while (read < data.Length)
            {
                int n = fs.Read(data, read, data.Length - read);
                if (n <= 0) break;
                read += n;
            }
            return data.AsSpan(0, read).IndexOf(needle) >= 0;
        }
    }
}
