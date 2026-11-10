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
        // ───────────────────────────── TASK-16b (ADR-66) Cockpit UI v2 Phase 2 ─────────────────────────────

        private const double MarkdownParseMaxMs = 1.0;   // spec E2: < 1.0 ms
        private const double HistoryQueryMaxMs = 2.0;    // spec E3: < 2.0 ms

        /// <summary>
        /// TASK-16b "ui-v2b" (headless, temp folders and a temp SQLite file only, 0 tokens, no network):
        ///   CUI-Markdown  counts of bold / code / code blocks / bullets / headings, edge cases never throw, 4 KB answer &lt; 1 ms
        ///   CUI-Layout    answer history on a TEMP database: only is_test = 0 rows with an answer, newest first, query &lt; 2 ms
        ///   CUI-Lazy      AllianceStatus next step (implement → Claude Code verify → Gemini review) from temp alliance files
        ///   MCP shift     shift_state.json sanitise / round trip / no BOM; pipe client returns at once without a Cockpit
        /// </summary>
        private static (object Report, bool Pass) UiV2b(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            string temp = Path.Combine(Path.GetTempPath(), "nexus_task16b_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                object markdown = UiV2bMarkdown(checks);
                object history = UiV2bHistory(temp, checks);
                object alliance = UiV2bAlliance(temp, checks);
                object shift = UiV2bShift(temp, checks);
                bool pass = checks.Values.All(v => v);
                return (new
                {
                    scenario = "ui-v2b",
                    pass,
                    failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                    checks,
                    markdown,
                    history,
                    alliance,
                    shift
                }, pass);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(temp, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static object UiV2bMarkdown(Dictionary<string, bool> checks)
        {
            const string Sample =
                "# Tối ưu FPS\n" +
                "Bước 1: **Ưu tiên tiến trình** game lên `High` và nền xuống `BelowNormal`.\n" +
                "\n" +
                "- Tắt **overlay** Discord\n" +
                "* Giữ GPU dưới `80%`\n" +
                "```csharp\n" +
                "var p = Process.GetCurrentProcess();\n" +
                "p.PriorityClass = ProcessPriorityClass.High; // **không in đậm trong code**\n" +
                "```\n" +
                "Xong.";
            MdDocument doc = LightweightMarkdown.Parse(Sample);
            MdBlock? code = doc.Blocks.FirstOrDefault(b => b.Kind == MdBlockKind.CodeBlock);
            checks["md.counts"] = doc.BoldSpans == 2 && doc.CodeSpans == 3 && doc.CodeBlocks == 1 && doc.Bullets == 2 && doc.Headings == 1;
            checks["md.codeBlockRaw"] = code != null && code.Language == "csharp" &&
                                        code.Code == "var p = Process.GetCurrentProcess();\np.PriorityClass = ProcessPriorityClass.High; // **không in đậm trong code**";
            MdBlock bold = doc.Blocks[1];
            checks["md.inlineOrder"] = bold.Spans.Count == 7 && bold.Spans[0] == new MdSpan(MdSpanKind.Text, "Bước 1: ") &&
                                       bold.Spans[1] == new MdSpan(MdSpanKind.Bold, "Ưu tiên tiến trình") &&
                                       bold.Spans[3] == new MdSpan(MdSpanKind.Code, "High") && bold.Spans[5] == new MdSpan(MdSpanKind.Code, "BelowNormal") &&
                                       bold.Spans[6] == new MdSpan(MdSpanKind.Text, ".");

            // edge cases: nothing throws, unmatched markers stay literal
            var edge = new Dictionary<string, object>();
            bool edgeOk = true;
            foreach (string? input in new[] { null, "", "**", "`", "**mở mà không đóng", "``", "```", "```js\nkhông đóng fence", "* ", "-", "#",
                                              "<>&\"'\t\\ 🚀 ✅ ` ** `", "****", "a**b**c`d`e" })
            {
                try
                {
                    MdDocument d = LightweightMarkdown.Parse(input);
                    string literal = string.Concat(d.Blocks.SelectMany(b => b.Spans).Select(s => s.Text));
                    edge[input ?? "(null)"] = new { blocks = d.Blocks.Count, d.BoldSpans, d.CodeSpans, d.CodeBlocks, literal };
                }
                catch (Exception ex)
                {
                    edgeOk = false;
                    edge[input ?? "(null)"] = ex.GetType().Name + ": " + ex.Message;
                }
            }
            MdDocument lone = LightweightMarkdown.Parse("**");
            MdDocument mixed = LightweightMarkdown.Parse("a**b**c`d`e");
            checks["md.edgeCases"] = edgeOk && LightweightMarkdown.Parse(null).Blocks.Count == 0 &&
                                     lone.BoldSpans == 0 && lone.Blocks[0].Spans[0].Text == "**" &&
                                     mixed.BoldSpans == 1 && mixed.CodeSpans == 1 && mixed.Blocks[0].Spans.Count == 5;

            // speed: a ~4 KB answer, median of 50 parses after one warm-up
            var big = new StringBuilder();
            while (big.Length < 4096) big.Append(Sample).Append('\n');
            string bigText = big.ToString();
            LightweightMarkdown.Parse(bigText);
            var times = new List<double>();
            for (int i = 0; i < 50; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                LightweightMarkdown.Parse(bigText);
                times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            times.Sort();
            double p50 = Math.Round(times[times.Count / 2], 4), p95 = Math.Round(times[(int)(times.Count * 0.95) - 1], 4);
            checks["md.parseUnder1ms"] = WithinSla("ui-v2b markdown 4 KB parse P95", p95, MarkdownParseMaxMs);
            return new { sample = new { doc.BoldSpans, doc.CodeSpans, doc.CodeBlocks, doc.Bullets, doc.Headings, blocks = doc.Blocks.Count }, edge,
                bigChars = bigText.Length, p50, p95, slaMs = MarkdownParseMaxMs };
        }

        private static object UiV2bHistory(string temp, Dictionary<string, bool> checks)
        {
            // A temporary database: rows are flagged here only, the user's nexus_memory.db is never touched by this part.
            var store = new SqliteMemoryStore(Path.Combine(temp, "history_test.db"));
            long a = store.InsertDispatchLog("Micro", "Squad1", "agent-a", "qa-a", "Câu hỏi thật 1");
            long b = store.InsertDispatchLog("Micro", "Squad1", "agent-b", "qa-b", "Câu hỏi thật 2");
            long c = store.InsertDispatchLog("Micro", "Squad1", "agent-c", "qa-c", "Chưa có câu trả lời");
            long d = store.InsertDispatchLog("Micro", "Squad1", "agent-d", "qa-d", "Bản ghi test (Gate 16b E3)");
            store.SaveAiResponse(a, "**Trả lời 1**", "gemini-x", 100, liveApi: false);
            store.SaveAiResponse(b, "Trả lời 2 `code`", "gemini-x", 120, liveApi: false);
            store.SaveAiResponse(d, "Trả lời test", "gemini-mock", 5, liveApi: false);
            bool flagged = store.SetDispatchTestFlag(a, false) && store.SetDispatchTestFlag(b, false) && store.SetDispatchTestFlag(c, false);
            // d keeps is_test = 1 (written during a selftest), so it must never show.
            IReadOnlyList<RecentDispatch> rows = store.GetAnswerHistory(8);
            var times = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                store.GetAnswerHistory(8);
                times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            times.Sort();
            double p50 = Math.Round(times[times.Count / 2], 3), p95 = Math.Round(times[(int)(times.Count * 0.95) - 1], 3);
            long[] ids = rows.Select(r => r.Id).ToArray();
            checks["hist.onlyRealAnswered"] = flagged && ids.SequenceEqual(new[] { b, a }) &&
                                              rows.All(r => !CockpitDisplayFilter.IsTestDispatch(r)) && rows[0].AiResponse == "Trả lời 2 `code`";
            checks["hist.queryUnder2ms"] = WithinSla("ui-v2b answer history query P95", p95, HistoryQueryMaxMs);
            return new { ids, expected = new[] { b, a }, testRowHidden = d, unansweredHidden = c, p50, p95, slaMs = HistoryQueryMaxMs };
        }

        private static object UiV2bAlliance(string temp, Dictionary<string, bool> checks)
        {
            string repo = Path.Combine(temp, "repo");
            string handoff = Path.Combine(repo, "03_Local_Data_Vault", "handoff");
            string config = Path.Combine(repo, "03_Local_Data_Vault", "config");
            Directory.CreateDirectory(handoff);
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var utf8 = new UTF8Encoding(false);
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/claude/task-77x-demo\n", utf8);
            File.WriteAllText(Path.Combine(handoff, "current_task.md"),
                "# CURRENT TASK SPECIFICATION — TASK-77x (ADR-99)\n\n- **Task ID:** `TASK-77x`\n- **Git Branch:** `claude/task-77x-demo`\n", utf8);

            AllianceSnapshot implement = AllianceStatus.Read(handoff, config);
            string handoffPath = Path.Combine(handoff, "gate77x_handoff.md");
            File.WriteAllText(handoffPath,
                "# Gate 77x\n\n## 1. Ca này\n\nx\n\n## 2. Trạng thái Gate 77x — ⏳ chưa đủ bằng chứng\n\n```powershell\n" +
                "powershell -File .\\tools\\Verify-Task77x.ps1\n```\n\n## 5. Lời nhắn dán cho Gemini Web\n\n> Chưa gửi\n", utf8);
            AllianceSnapshot verify = AllianceStatus.Read(handoff, config);
            File.WriteAllText(handoffPath,
                "# Gate 77x\n\n## 2. Trạng thái Gate 77x — ✅ E1–E5 ALL PASS\n\nok\n\n## 5. Lời nhắn dán cho Gemini Web\n\n" +
                "> Chào Gemini, **Gate 77x ALL PASS**.\n>\n> - RAM 35 MB\n\n## 6. Khác\n\n> không lấy dòng này\n", utf8);
            File.WriteAllText(Path.Combine(handoff, "task77x_verification.json"), "{ \"allPassed\": true, \"timestamp\": \"2026-11-10T00:00:00Z\" }", utf8);
            ShiftStateFile.Write(config, ShiftStateFile.Sanitize("Claude Code", "Gate 77x", "ALL PASS", "xong", "selftest"));
            AllianceSnapshot review = AllianceStatus.Read(handoff, config);

            checks["alliance.parse"] = implement.TaskId == "TASK-77x" && implement.Adr == "ADR-99" && implement.SpecBranch == "claude/task-77x-demo" &&
                                       implement.GitBranch == "claude/task-77x-demo";
            checks["alliance.implement"] = implement.NextStep == AllianceStatus.StepImplement && implement.GateState == "none" &&
                                           implement.ClipboardText.Contains("claude/task-77x-demo", StringComparison.Ordinal) &&
                                           implement.ClipboardText.Contains("Verify-Task77x.ps1", StringComparison.Ordinal);
            checks["alliance.verify"] = verify.NextStep == AllianceStatus.StepVerify && verify.GateState == "pending" &&
                                        verify.ClipboardText.Contains("powershell -File .\\tools\\Verify-Task77x.ps1", StringComparison.Ordinal);
            checks["alliance.review"] = review.NextStep == AllianceStatus.StepReview && review.GateState == "pass" &&
                                        review.ClipboardText == "Chào Gemini, **Gate 77x ALL PASS**.\n\n- RAM 35 MB" &&
                                        review.LatestVerificationPassed == true && review.Shift?.Gate == "Gate 77x";
            checks["alliance.readUnder5ms"] = review.ElapsedMs < 5.0;
            return new { implement, verify, review };
        }

        private static object UiV2bShift(string temp, Dictionary<string, bool> checks)
        {
            string dir = Path.Combine(temp, "shift");
            Directory.CreateDirectory(dir);
            string fakeSecret = "sk-" + new string('b', 24); // built at run time, never a literal
            ShiftState redacted = ShiftStateFile.Sanitize(null, "Gate 16b", "ALL PASS", $"khóa {fakeSecret}", "selftest");
            ShiftState capped = ShiftStateFile.Sanitize("Claude Code", "Gate 16b", "ALL PASS", new string('x', 1000), "selftest");
            string? error = ShiftStateFile.Write(dir, capped);
            ShiftState? back = ShiftStateFile.TryRead(dir);
            bool noBom = NoBom(Path.Combine(dir, ShiftStateFile.FileName));
            ShiftState? viaJson = ShiftStateFile.FromJson(ShiftStateFile.ToJson(redacted));
            checks["shift.sanitize"] = redacted.Agent == "Claude Code" && !redacted.Summary.Contains(fakeSecret, StringComparison.Ordinal) &&
                                       capped.Summary.Length == ShiftStateFile.MaxSummaryChars && ShiftStateFile.FromJson("{ hỏng") == null;
            checks["shift.roundTrip"] = error == null && back == capped && noBom && viaJson == redacted;

            long t0 = Stopwatch.GetTimestamp();
            string? reply = CockpitPipeClient.Send("PING16B", null, connectTimeoutMs: 200, readTimeoutMs: 1000);
            double pipeMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 1);
            checks["shift.pipeNoBlock"] = pipeMs < 1500 && (reply == null || reply.StartsWith("ERR:", StringComparison.Ordinal));
            bool image = ClipboardService.ProbeImage(out uint sequence);
            return new { redacted, cappedChars = capped.Summary.Length, back, noBom, pipeReply = reply, pipeMs, clipboardImage = image, clipboardSequence = sequence };
        }
    }
}
