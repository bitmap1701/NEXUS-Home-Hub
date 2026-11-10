using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NEXUSHome.CockpitUI;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-16c (ADR-68) Cockpit UI v2 visual layer ─────────────────────────────

        private const double MockCancelDelayMs = 3000;   // the mock "network" wait that Hủy has to cut short
        private const double CancelReturnMaxMs = 1500;   // the cancelled request must end well before the 3 s mock delay

        /// <summary>
        /// TASK-16c "ui-v2-visual" (mock Gemini, is_test rows only, 0 live tokens, cockpit_ui.json put back):
        ///   Core     the "/" catalogue (17 commands of the spec + /f), filtering + VỪA DÙNG, "/" and "@" detection, the agent filter
        ///            on a synthetic registry (the 283 agents live outside the repository), "@&lt;agentId&gt; task" dispatches to that
        ///            agent, and Hủy: an /ask! with a 3 s mock delay cancelled by its CancellationTokenSource ends at once as [ĐÃ HỦY]
        ///   Window   the real CockpitShellWindow, never shown (Measure / Arrange only): 2 columns + 300 px right column, 44 px top
        ///            bar, the single 40 px line, Card Pool 30 → 20 views, the local "cục bộ · 0 token" line, the running card +
        ///            Hủy, the / and @ palettes, the lazy and image cards, the F1 quick switches and the Alt+A alliance board
        /// </summary>
        private static (object Report, bool Pass) UiV2Visual(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            string? oldMock = Environment.GetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable);
            string? oldDelay = Environment.GetEnvironmentVariable(GeminiAgentClient.MockDelayEnvironmentVariable);
            if (string.IsNullOrEmpty(oldMock)) Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, "Gate 16c visual mock");
            string uiPath = Path.Combine(runtime.ConfigDirectory, CockpitUiStore.FileName);
            byte[]? uiBackup = null;
            try { if (File.Exists(uiPath)) uiBackup = File.ReadAllBytes(uiPath); }
            catch (IOException) { uiBackup = null; }
            try
            {
                runtime.QuotaGuard.ResetSlidingWindows();
                var sampleAgents = new List<AgentProfile>
                {
                    new("security-architect", "Security Architect", "security", "Threat models", "x", "x"),
                    new("secrets-credential-hygiene-engineer", "Secrets & Credential Hygiene Engineer", "security", "Secrets", "x", "x"),
                    new("penetration-tester", "Penetration Tester", "security", "Pen tests", "x", "x"),
                    new("backend-architect", "Backend Architect", "engineering", "APIs", "x", "x"),
                    new("desktop-app-engineer", "Desktop App Engineer", "engineering", "WPF", "x", "x"),
                    new("placeholder-sec", "Security Placeholder", "security", "-", "x", "x", IsPlaceholder: true)
                };
                object core = UiV2VisualCore(runtime, sampleAgents, checks);
                object cancel = UiV2VisualCancel(runtime, checks);

                (object Report, bool Pass) window;
                CockpitShellWindow? w = null;
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    w = new CockpitShellWindow(runtime);
                    window = w.RunVisualSelfCheck(sampleAgents);
                }
                finally
                {
                    w?.Close();
                }
                checks["window.selfCheck"] = window.Pass;
                double windowMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 1);

                bool pass = checks.Values.All(v => v);
                return (new
                {
                    scenario = "ui-v2-visual",
                    pass,
                    failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                    checks,
                    core,
                    cancel,
                    windowMs,
                    window = window.Report
                }, pass);
            }
            finally
            {
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockEnvironmentVariable, oldMock);
                Environment.SetEnvironmentVariable(GeminiAgentClient.MockDelayEnvironmentVariable, oldDelay);
                try
                {
                    if (uiBackup != null) File.WriteAllBytes(uiPath, uiBackup);
                    else if (File.Exists(uiPath)) File.Delete(uiPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static object UiV2VisualCore(NexusRuntime runtime, IReadOnlyList<AgentProfile> sampleAgents, Dictionary<string, bool> checks)
        {
            string[] specCommands = { "/img", "/calc", "/ask", "/g", "/snap", "/pin", "/ctx", "/timer", "/note", "/copy", "/unpin", "/qa", "/retry", "/mem", "/status", "/boost", "/ui" };
            string[] missing = specCommands.Where(c => CockpitPalette.Commands.All(x => x.Name != c)).ToArray();
            checks["core.catalogue"] = missing.Length == 0 && CockpitPalette.Commands.All(c => c.Description.Length > 0 && c.Example.StartsWith(c.Name, StringComparison.Ordinal));

            var (_, allEmpty) = CockpitPalette.FilterCommands("", null, 50);
            var (_, ca) = CockpitPalette.FilterCommands("ca", null, 12);
            var recentList = new List<string>();
            CockpitPalette.NoteRecent(recentList, "/calc 1+1");
            CockpitPalette.NoteRecent(recentList, "/img lỗi gì?");
            CockpitPalette.NoteRecent(recentList, "hello"); // not a command: ignored
            var (recentHits, rest) = CockpitPalette.FilterCommands("", recentList, 50);
            checks["core.filter"] = allEmpty.Count == CockpitPalette.Commands.Count && ca.Count > 0 && ca[0].Name == "/calc" &&
                                    recentHits.Select(c => c.Name).SequenceEqual(new[] { "/img", "/calc" }) && rest.All(c => c.Name != "/img" && c.Name != "/calc");

            PaletteQuery? slash = CockpitPalette.Detect("/ca");
            PaletteQuery? slashArgs = CockpitPalette.Detect("/ask câu hỏi");
            PaletteQuery? at = CockpitPalette.Detect("@sec");
            PaletteQuery? atMid = CockpitPalette.Detect("giúp tôi @sec");
            PaletteQuery? atDone = CockpitPalette.Detect("@security-architect rà soát");
            PaletteQuery? mail = CockpitPalette.Detect("bitmap@example");
            checks["core.detect"] = slash is { Mode: CockpitPalette.SlashMode, Query: "ca" } && slashArgs == null &&
                                    at is { Mode: CockpitPalette.AgentMode, Query: "sec", TokenStart: 0 } &&
                                    atMid is { Mode: CockpitPalette.AgentMode, Query: "sec", TokenStart: 9 } && atDone == null && mail == null;

            IReadOnlyList<PaletteAgent> sec = CockpitPalette.FilterAgents(sampleAgents, "sec", 8);
            // name prefix first (both "Sec…" names, A→Z), then the division match; placeholders never shown
            checks["core.agentFilterSec"] = sec.Count == 3 && sec.Take(2).All(a => a.Name.StartsWith("Sec", StringComparison.Ordinal)) &&
                                            sec.Any(a => a.Name == "Security Architect") && sec[2].Name == "Penetration Tester" &&
                                            sec.All(a => a.Division == "security") && sec.All(a => a.Id != "placeholder-sec");

            // "@<agentId> task" → that agent (only when the registry has a real agent; the 283 agents are on Nhat Hoa's PC)
            AgentProfile? real = runtime.Registry.GetAll().FirstOrDefault(a => !a.IsPlaceholder);
            object? atDispatch = null;
            if (real != null)
            {
                var processor = new CockpitCommandProcessor(runtime);
                CockpitCommandResult r = processor.Execute($"@{real.Id} kiểm thử chỉ định agent Gate 16c", IntPtr.Zero);
                bool ok = r.Ok && r.Dispatch != null && string.Equals(r.Dispatch.PrimaryAgent.Id, real.Id, StringComparison.OrdinalIgnoreCase);
                checks["core.atAgentDispatch"] = ok;
                atDispatch = new { agent = real.Id, ok, dispatchId = r.Dispatch?.DispatchId, primary = r.Dispatch?.PrimaryAgent.Id, message = r.Message };
            }

            return new
            {
                commands = CockpitPalette.Commands.Count,
                missing,
                caFirst = ca.FirstOrDefault()?.Name,
                recent = recentHits.Select(c => c.Name).ToArray(),
                agentHits = sec.Select(a => a.Name).ToArray(),
                registryAgents = runtime.Registry.Count,
                atDispatch
            };
        }

        /// <summary>Decision 3 end to end (Core): /ask! with a 3 s mock delay, cancelled after ~150 ms, ends as [ĐÃ HỦY] well before 3 s.</summary>
        private static object UiV2VisualCancel(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockDelayEnvironmentVariable, MockCancelDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var processor = new CockpitCommandProcessor(runtime);
            string marker = "huy" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            CockpitCommandResult r = processor.Execute($"/ask! Gate 16c kiểm thử Hủy {marker}", IntPtr.Zero);
            bool pendingAtStart = r.AiTask is { IsCompleted: false } && r.Cancellation != null;
            System.Threading.Thread.Sleep(150);
            double cancelAtMs = sw.Elapsed.TotalMilliseconds;
            r.Cancellation?.Cancel();
            ConsultOutcome? outcome = null;
            try
            {
                if (r.AiTask != null && r.AiTask.Wait(TimeSpan.FromSeconds(10))) outcome = r.AiTask.Result;
            }
            catch (AggregateException) { outcome = null; }
            double endMs = sw.Elapsed.TotalMilliseconds;
            Environment.SetEnvironmentVariable(GeminiAgentClient.MockDelayEnvironmentVariable, null);
            bool cancelledText = outcome != null && !outcome.AiOk && outcome.AiResponse.StartsWith("[ĐÃ HỦY]", StringComparison.Ordinal);
            checks["cancel.pendingWithToken"] = pendingAtStart;
            checks["cancel.endsAsCancelled"] = cancelledText;
            checks["cancel.fast"] = outcome != null && endMs - cancelAtMs < CancelReturnMaxMs && endMs < MockCancelDelayMs;
            checks["cancel.zeroTokens"] = outcome != null && outcome.TotalTokens == 0;
            return new
            {
                ok = r.Ok,
                kind = r.Kind,
                pendingAtStart,
                cancelAtMs = Math.Round(cancelAtMs, 1),
                endMs = Math.Round(endMs, 1),
                afterCancelMs = Math.Round(endMs - cancelAtMs, 1),
                dispatchId = outcome?.DispatchId,
                answer = outcome?.AiResponse,
                tokens = outcome?.TotalTokens
            };
        }
    }
}
