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
        // ───────────────────────────── TASK-13 (HUD micro-widgets, Right MFD pins) ─────────────────────────────

        private const int CopySamples = 30;
        private const double CopyP50MaxMs = 1.5;
        private const double CopyP95MaxMs = 2.5;
        private const double Extract4KbMaxMs = 0.5;

        /// <summary>Gate 13 test answer: 2 code blocks (csharp, powershell) + 1 checklist (3 items).</summary>
        internal static readonly string Gate13Response = string.Join("\n", new[]
        {
            "Đây là khung TCP server cho lab UTH.",
            "```csharp",
            "var listener = new TcpListener(IPAddress.Any, 8888);",
            "listener.Start();",
            "Console.WriteLine(\"listening on 8888\");",
            "```",
            "Kiểm tra trước khi chạy:",
            "- [ ] Mở cổng 8888 trên Windows Firewall",
            "- [x] Build Release -warnaserror",
            "- [ ] Chạy Verify-Task13.ps1",
            "Thử kết nối:",
            "~~~powershell",
            "Test-NetConnection -ComputerName 127.0.0.1 -Port 8888",
            "~~~",
            "Xong."
        });

        internal const string Gate13CodeCSharp = "var listener = new TcpListener(IPAddress.Any, 8888);\nlistener.Start();\nConsole.WriteLine(\"listening on 8888\");";
        internal const string Gate13CodePowerShell = "Test-NetConnection -ComputerName 127.0.0.1 -Port 8888";

        /// <summary>Writes a dispatch snapshot whose AI answer is <paramref name="text"/> (the "current response").</summary>
        private static string? WriteTestResponse(NexusRuntime runtime, string text)
        {
            LatestDispatchSnapshot snapshot = LatestDispatchFile.FromLocalUtility(LatestDispatchFile.LocalUtilityEvent, LocalUtilityService.Calc,
                    "/ask Gate 13 widget test", text, "PASSED", runtime.Source, 0)
                with
                {
                    Event = "ai_response",
                    LocalUtility = null,
                    DispatchId = 1,
                    AiModel = "selftest",
                    TaskSummary = "Gate 13 widget test"
                };
            return LatestDispatchFile.Write(runtime.LatestDispatchPath, snapshot);
        }

        /// <summary>
        /// E2 (headless): extraction of 2 code blocks + 1 checklist, fence-free content, 4 KB extraction &lt;= 0.5 ms,
        /// /copy 1 and /copy 2 equal to each block, 30 /copy calls P50 &lt;= 1.5 ms / P95 &lt;= 2.5 ms, extractedCards and
        /// pinnedCardsCount in latest_dispatch.json, no compiled-Regex option anywhere in the sources.
        /// </summary>
        private static (object, bool) Widgets(NexusRuntime runtime)
        {
            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            string? writeError = WriteTestResponse(runtime, Gate13Response);

            IReadOnlyList<HudWidgetCard> cards = runtime.HudWidgets.Cards;
            var code = cards.Where(c => c.Type == "code").ToList();
            var checklists = cards.Where(c => c.Type == "checklist").ToList();
            bool extractOk = writeError == null && code.Count == 2 && checklists.Count == 1 &&
                             code[0].Language == "csharp" && code[1].Language == "powershell" &&
                             code[0].Content == Gate13CodeCSharp && code[1].Content == Gate13CodePowerShell &&
                             !code[0].Content.Contains("```", StringComparison.Ordinal) && !code[1].Content.Contains("~~~", StringComparison.Ordinal) &&
                             code[0].LineCount == 3 && checklists[0].LineCount == 3 && checklists[0].Title.StartsWith("checklist 1/3", StringComparison.Ordinal);

            // 4 KB answer: the same blocks repeated; extraction alone, 1 warm-up + 20 samples.
            var big = new StringBuilder();
            while (big.Length < 4096) big.Append(Gate13Response).Append('\n');
            string bigText = big.ToString();
            WidgetCardExtractor.Extract(bigText);
            var extractSamples = new List<double>();
            int bigCards = 0;
            for (int i = 0; i < 20; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                bigCards = WidgetCardExtractor.Extract(bigText).Count;
                extractSamples.Add(Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 4));
            }
            LatencyStats extract = LatencyStats.Of(extractSamples);
            bool extractFastOk = bigCards > 0 && WithinSla("WidgetCardExtractor 4 KB P50", extract.P50, Extract4KbMaxMs);

            CockpitCommandResult copy1 = processor.Execute("/copy 1", IntPtr.Zero);
            string after1 = ClipboardService.LastCopiedText;
            CockpitCommandResult copy2 = processor.Execute("/copy 2", IntPtr.Zero);
            string after2 = ClipboardService.LastCopiedText;
            CockpitCommandResult copyDefault = processor.Execute("/copy", IntPtr.Zero);
            string afterDefault = ClipboardService.LastCopiedText;
            CockpitCommandResult copy9 = processor.Execute("/copy 9", IntPtr.Zero);
            bool copyOk = copy1.Ok && after1 == Gate13CodeCSharp && copy2.Ok && after2 == Gate13CodePowerShell &&
                          copyDefault.Ok && afterDefault == Gate13CodeCSharp && !copy9.Ok;

            var copyTimes = new List<double>(CopySamples);
            bool samplesOk = true;
            for (int i = 0; i < CopySamples; i++)
            {
                CockpitCommandResult r = processor.Execute(i % 2 == 0 ? "/copy 1" : "/copy 2", IntPtr.Zero);
                samplesOk &= r.Ok;
                copyTimes.Add(r.ElapsedMs);
            }
            LatencyStats copyStats = LatencyStats.Of(copyTimes);
            bool copyLatencyOk = samplesOk && WithinSla("/copy total P50", copyStats.P50, CopyP50MaxMs) && WithinSla("/copy total P95", copyStats.P95, CopyP95MaxMs);

            LatestDispatchSnapshot? snap = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            bool snapshotOk = snap?.ExtractedCards != null && snap.ExtractedCards.Count == 3 && snap.ExtractedCards[0].Type == "code" &&
                              snap.ExtractedCards[0].Language == "csharp" && snap.PinnedCardsCount.HasValue;

            // ADR-29: no compiled-Regex option in any source file of the engine.
            string engineRoot = Path.GetFullPath(Path.Combine(runtime.VaultPath, "..", "01_Foundation_Engine"));
            var compiledHits = Directory.Exists(engineRoot)
                ? Directory.EnumerateFiles(engineRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .Where(f => File.ReadAllText(f).Contains("RegexOptions." + "Compiled", StringComparison.Ordinal))
                    .Select(f => Path.GetFileName(f)).ToList()
                : new List<string> { "(engine folder not found)" };
            bool noCompiledRegex = compiledHits.Count == 0;

            var checks = new { extractOk, extractFastOk, copyOk, copyLatencyOk, snapshotOk, noCompiledRegex };
            bool pass = extractOk && extractFastOk && copyOk && copyLatencyOk && snapshotOk && noCompiledRegex;
            return (new
            {
                scenario = "widgets",
                pass,
                checks,
                cards = cards.Select(c => new { c.Index, c.Type, c.Title, c.Language, c.LineCount, c.PreviewText }),
                indicator = WidgetCardExtractor.Indicator(cards),
                extract4Kb = new { chars = bigText.Length, cards = bigCards, latency = extract, slaMs = Extract4KbMaxMs },
                copy = new { copy1 = copy1.Message, copy2 = copy2.Message, copyDefault = copyDefault.Message, copy9 = copy9.Message, latency = copyStats, p50MaxMs = CopyP50MaxMs, p95MaxMs = CopyP95MaxMs },
                extractedCards = snap?.ExtractedCards,
                pinnedCardsCount = snap?.PinnedCardsCount,
                compiledRegexFiles = compiledHits
            }, pass);
        }

        /// <summary>
        /// E3 (headless): /unpin all -> /pin #1 -> /pin "Cổng TCP Socket UTH: 8888" -> /pins = 2; mfd_pins.json without BOM;
        /// 6 more pins keep exactly maxPinnedCards (FIFO); a key-shaped pin is refused and never written. The user's own
        /// mfd_pins.json is restored at the end.
        /// </summary>
        private static (object, bool) MfdPins(NexusRuntime runtime)
        {
            runtime.WarmUp();
            var processor = new CockpitCommandProcessor(runtime);
            MfdPinManager pins = runtime.Pins;
            byte[]? original = File.Exists(pins.PinsPath) ? File.ReadAllBytes(pins.PinsPath) : null;
            string? writeError = WriteTestResponse(runtime, Gate13Response);
            int max = pins.MaxPins;
            try
            {
                CockpitCommandResult clear = processor.Execute("/unpin all", IntPtr.Zero);
                CockpitCommandResult pinCard = processor.Execute("/pin #1", IntPtr.Zero);
                CockpitCommandResult pinNote = processor.Execute("/pin Cổng TCP Socket UTH: 8888", IntPtr.Zero);
                CockpitCommandResult list = processor.Execute("/pins", IntPtr.Zero);
                IReadOnlyList<MfdPin> two = pins.List();
                byte[] bytes = File.ReadAllBytes(pins.PinsPath);
                bool noBom = !(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
                bool lifecycleOk = writeError == null && clear.Ok && pinCard.Ok && pinNote.Ok && list.Ok && two.Count == 2 &&
                                   two[0].Type == "code" && two[0].Content == Gate13CodeCSharp && two[1].Content == "Cổng TCP Socket UTH: 8888" &&
                                   list.Message.StartsWith("[PINS] 2/", StringComparison.Ordinal) && noBom;

                var fifo = new List<string>();
                for (int i = 1; i <= 6; i++)
                {
                    CockpitCommandResult r = processor.Execute($"/pin Gate 13 FIFO {i}", IntPtr.Zero);
                    fifo.Add($"{r.Ok}:{pins.Count}");
                }
                IReadOnlyList<MfdPin> capped = pins.List();
                bool fifoOk = capped.Count == max && capped[^1].Content == "Gate 13 FIFO 6" &&
                              capped.Select(p => p.PinId).SequenceEqual(Enumerable.Range(1, max));

                string dummy = "AIza" + "SyDummySecretKeyLeakTest123456789";
                CockpitCommandResult secret = processor.Execute($"/pin {dummy}", IntPtr.Zero);
                MfdPinResult secretDirect = pins.Pin("note", "x", null, $"key {dummy}", "selftest");
                string fileText = File.ReadAllText(pins.PinsPath, Encoding.UTF8);
                bool secretOk = !secret.Ok && secret.Kind == "blocked" && secret.Message.Contains("Secret Guard", StringComparison.Ordinal) &&
                                !secretDirect.Ok && secretDirect.Blocked && !fileText.Contains("SyDummySecretKeyLeakTest", StringComparison.Ordinal) &&
                                pins.Count == max;

                CockpitCommandResult unpinOne = processor.Execute("/unpin 1", IntPtr.Zero);
                bool unpinOk = unpinOne.Ok && pins.Count == max - 1;
                CockpitCommandResult unpinBad = processor.Execute("/unpin 99", IntPtr.Zero);
                CockpitCommandResult clearAll = processor.Execute("/pin clear", IntPtr.Zero);
                bool clearOk = !unpinBad.Ok && clearAll.Ok && pins.Count == 0;

                var checks = new { lifecycleOk, fifoOk, secretOk, unpinOk, clearOk };
                bool pass = lifecycleOk && fifoOk && secretOk && unpinOk && clearOk;
                return (new
                {
                    scenario = "mfd-pins",
                    pass,
                    checks,
                    maxPinnedCards = max,
                    pinCard = pinCard.Message,
                    pinNote = pinNote.Message,
                    list = list.Message,
                    fifo,
                    cappedTitles = capped.Select(p => $"#{p.PinId} {p.Title}"),
                    secret = secret.Message,
                    unpin = unpinOne.Message,
                    clear = clearAll.Message,
                    noBom
                }, pass);
            }
            finally
            {
                // put the user's own pins back (or leave an empty board if there was no file)
                if (original != null) AtomicFile.WriteAllBytes(pins.PinsPath, original);
                else pins.Unpin("all");
            }
        }
    }
}
