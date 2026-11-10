using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-16b MCP notify_shift_complete (ADR-66): the last shift a member reported (Claude Code, Claude, Antigravity).
    /// Saved in config/shift_state.json (per machine, Git-ignored, UTF-8 without BOM) and pushed to a running Cockpit.
    /// </summary>
    public sealed record ShiftState(
        [property: JsonPropertyName("agent")] string Agent,
        [property: JsonPropertyName("gate")] string Gate,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("atUtc")] string AtUtc,
        [property: JsonPropertyName("source")] string Source = "McpTools");

    public static class ShiftStateFile
    {
        public const string FileName = "shift_state.json";
        public const int MaxSummaryChars = 240;

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>Trims the fields, caps the summary and replaces anything that looks like a secret.</summary>
        public static ShiftState Sanitize(string? agent, string? gate, string? status, string? summary, string source)
        {
            static string Clean(string? s, string fallback, int max)
            {
                string v = (s ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (v.Length == 0) v = fallback;
                return v.Length > max ? v.Substring(0, max - 1) + "…" : v;
            }
            string text = Clean(summary, "(không có tóm tắt)", MaxSummaryChars);
            if (LocalNoteService.ContainsSecret(text)) text = "[nội dung ẩn: giống khóa bí mật]";
            return new ShiftState(Clean(agent, "Claude Code", 40), Clean(gate, "?", 40), Clean(status, "DONE", 40), text,
                DateTime.UtcNow.ToString("o"), source);
        }

        public static string? Write(string configDirectory, ShiftState state) =>
            AtomicFile.WriteAllBytes(Path.Combine(configDirectory, FileName), new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(state, Json)));

        public static ShiftState? TryRead(string configDirectory)
        {
            try
            {
                string path = Path.Combine(configDirectory, FileName);
                return File.Exists(path) ? JsonSerializer.Deserialize<ShiftState>(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public static string ToJson(ShiftState state) => JsonSerializer.Serialize(state);

        public static ShiftState? FromJson(string? json)
        {
            try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ShiftState>(json); }
            catch (JsonException) { return null; }
        }
    }

    /// <summary>One request to a running Cockpit over NEXUS_Home_Cockpit_Pipe (McpTools → HostService). Never throws.</summary>
    public static class CockpitPipeClient
    {
        public const string PipeName = "NEXUS_Home_Cockpit_Pipe";

        /// <returns>The "result" string of the HostService answer, or null when no Cockpit is running / it did not answer.</returns>
        public static string? Send(string cmd, string? text, int connectTimeoutMs = 300, int readTimeoutMs = 2000)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(connectTimeoutMs);
                var utf8 = new UTF8Encoding(false);
                using var writer = new StreamWriter(client, utf8, 1024, leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(client, utf8, false, 1024, leaveOpen: true);
                string request = text == null
                    ? JsonSerializer.Serialize(new { cmd })
                    : JsonSerializer.Serialize(new { cmd, text });
                writer.WriteLine(request);
                System.Threading.Tasks.Task<string?> line = reader.ReadLineAsync();
                if (!line.Wait(readTimeoutMs)) return null;
                using JsonDocument doc = JsonDocument.Parse(line.Result ?? "{}");
                return doc.RootElement.TryGetProperty("result", out JsonElement r) ? r.GetString() : null;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or InvalidOperationException
                                           or UnauthorizedAccessException or AggregateException)
            {
                return null;
            }
        }
    }

    /// <summary>TASK-16b CUI-Alliance / CUI-Lazy: where the alliance loop stands and what to paste next.</summary>
    public sealed record AllianceSnapshot(
        string? TaskId,
        string? Adr,
        string? SpecBranch,
        string? GitBranch,
        string? HandoffFile,
        string GateState,
        string? LatestVerificationFile,
        bool? LatestVerificationPassed,
        string? LatestVerificationAt,
        string NextStep,
        string NextStepLabel,
        string ClipboardText,
        ShiftState? Shift,
        double ElapsedMs);

    /// <summary>
    /// TASK-16b (ADR-66): reads the alliance files of this repository (no network, no git process, read only):
    /// current_task.md (task id, ADR, branch), the matching gate&lt;N&gt;_handoff.md (section 2 = Gate state, section 5 = message
    /// for Gemini), the newest task*_verification.json, .git/HEAD and config/shift_state.json. Next step:
    ///   no handoff for the current task       → "claude-implement"   (prompt for Claude: pull the branch, implement the spec)
    ///   handoff, Gate not passed yet           → "claude-code-verify" (prompt for Claude Code: the Verify commands of section 2)
    ///   handoff, Gate ALL PASS                 → "gemini-review"      (section 5 of the handoff, the message for Gemini)
    /// The spec said "from latest_dispatch.json"; that file holds the last Cockpit answer, not the Gate, so the handoff files
    /// are read instead (they are what the alliance actually copies by hand today).
    /// </summary>
    /// <summary>TASK-16c board 3: one Gate check box (E1..E5) of the newest verification report of the current task.</summary>
    public sealed record AllianceGateCheck(string Id, bool? Pass);

    /// <summary>
    /// TASK-16c (ADR-68) design board 3 "LIÊN MINH // ĐIỀU PHỐI" (Alt+A): everything the board shows, read from files only
    /// (0 token). <see cref="GeminiMessage"/> = section 5 of the handoff (Alt+G), <see cref="ClaudeCodePrompt"/> = the Verify
    /// prompt built from section 2 (Alt+K), <see cref="ClosedGates"/> = handoffs whose section 2 says ALL PASS.
    /// </summary>
    public sealed record AllianceBoard(
        AllianceSnapshot Snapshot,
        string GateTitle,
        IReadOnlyList<AllianceGateCheck> Checks,
        string? ChecksFile,
        string GeminiMessage,
        string ClaudeCodePrompt,
        IReadOnlyList<string> ClosedGates,
        double ElapsedMs);

    public static class AllianceStatus
    {
        public const string StepImplement = "claude-implement";
        public const string StepVerify = "claude-code-verify";
        public const string StepReview = "gemini-review";

        public static AllianceSnapshot Read(string handoffDirectory, string? configDirectory = null)
        {
            long t0 = Stopwatch.GetTimestamp();
            string repoRoot = Path.GetFullPath(Path.Combine(handoffDirectory, "..", ".."));
            string spec = ReadText(Path.Combine(handoffDirectory, "current_task.md"));
            string head = string.Join("\n", spec.Split('\n').Take(20));
            string? taskId = FirstToken(head, "TASK-", allowSuffixLetter: true);
            string? adr = FirstToken(head, "ADR-", allowSuffixLetter: false);
            string? specBranch = FirstBranch(head);
            string? gitBranch = ReadGitBranch(repoRoot);
            string? number = taskId?.Substring("TASK-".Length).ToLowerInvariant();
            string? handoffName = number == null ? null : $"gate{number}_handoff.md";
            string handoffPath = handoffName == null ? string.Empty : Path.Combine(handoffDirectory, handoffName);
            string handoff = handoffName == null ? string.Empty : ReadText(handoffPath);

            string gateState = "none";
            if (handoff.Length > 0)
            {
                string section2 = Section(handoff, "## 2.");
                string title = section2.Split('\n').FirstOrDefault() ?? string.Empty;
                bool waiting = title.Contains("⏳", StringComparison.Ordinal) || title.Contains("chưa", StringComparison.OrdinalIgnoreCase);
                bool passed = title.Contains("✅", StringComparison.Ordinal) || title.Contains("ALL PASS", StringComparison.OrdinalIgnoreCase);
                gateState = passed && !waiting ? "pass" : "pending";
            }

            (string? verFile, bool? verPass, string? verAt) = LatestVerification(handoffDirectory);
            ShiftState? shift = configDirectory == null ? null : ShiftStateFile.TryRead(configDirectory);
            string branch = specBranch ?? gitBranch ?? "<nhánh>";
            string task = taskId ?? "TASK-?";
            string verifyScript = number == null ? "Verify-Task?.ps1" : $"Verify-Task{number}.ps1";

            string step, label, text;
            if (handoff.Length == 0)
            {
                step = StepImplement;
                label = $"Gửi Claude: triển khai {task}";
                text = $"Pull nhánh {branch} và triển khai {task}{(adr != null ? $" ({adr})" : string.Empty)} theo " +
                       $"03_Local_Data_Vault/handoff/current_task.md, kèm {verifyScript}. Cuối ca làm đúng CLAUDE.md (qa_report.md, " +
                       $"gate{number}_handoff.md, commit, push, lời nhắn cho Gemini).";
            }
            else if (gateState != "pass")
            {
                step = StepVerify;
                label = $"Gửi Claude Code: chạy Verify {task}";
                text = VerifyPrompt(handoff, branch, verifyScript, handoffName);
            }
            else
            {
                step = StepReview;
                label = $"Gửi Gemini: nghiệm thu {task}";
                text = BlockQuote(Section(handoff, "## 5."));
                if (text.Length == 0) text = $"Chào Gemini, {task} đã xong; chi tiết ở {handoffName}.";
            }

            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            return new AllianceSnapshot(taskId, adr, specBranch, gitBranch, handoff.Length > 0 ? handoffName : null, gateState,
                verFile, verPass, verAt, step, label, text, shift, ms);
        }

        private static string VerifyPrompt(string handoff, string branch, string verifyScript, string? handoffName)
        {
            string commands = FirstCodeBlock(Section(handoff, "## 2."));
            if (commands.Length == 0)
                commands = $"powershell -NoProfile -ExecutionPolicy Bypass -File .\\01_Foundation_Engine\\tools\\{verifyScript}";
            return $"Pull nhánh {branch} và chạy:\n{commands}\nTrong lúc chạy đừng chạy lệnh git, đừng bấm vào HUD, để yên chuột. " +
                   $"Sau đó ghi kết quả vào qa_report.md và mục 2 của {handoffName}, commit và push, rồi gọi MCP notify_shift_complete.";
        }

        /// <summary>
        /// TASK-16c board 3 (Alt+A): the snapshot plus the E1..E5 boxes of task&lt;N&gt;_verification.json, both texts to copy
        /// (Alt+G: section 5 for Gemini, Alt+K: the Verify prompt for Claude Code) and the Gates already closed. Read only.
        /// </summary>
        public static AllianceBoard ReadBoard(string handoffDirectory, string? configDirectory = null)
        {
            long t0 = Stopwatch.GetTimestamp();
            AllianceSnapshot s = Read(handoffDirectory, configDirectory);
            string? number = s.TaskId?.Substring("TASK-".Length).ToLowerInvariant();
            string handoff = s.HandoffFile == null ? string.Empty : ReadText(Path.Combine(handoffDirectory, s.HandoffFile));
            string branch = s.SpecBranch ?? s.GitBranch ?? "<nhánh>";
            string verifyScript = number == null ? "Verify-Task?.ps1" : $"Verify-Task{number}.ps1";
            string gemini = BlockQuote(Section(handoff, "## 5."));
            if (gemini.Length == 0) gemini = $"Chào Gemini, {s.TaskId ?? "TASK-?"} chưa có lời nhắn (mục 5 của {s.HandoffFile ?? "gate<N>_handoff.md"}).";
            string claudeCode = VerifyPrompt(handoff, branch, verifyScript, s.HandoffFile ?? (number == null ? null : $"gate{number}_handoff.md"));

            string? checksFile = number == null ? null : $"task{number}_verification.json";
            IReadOnlyList<AllianceGateCheck> checks = ReadChecks(checksFile == null ? null : Path.Combine(handoffDirectory, checksFile));
            if (checks.All(c => c.Pass == null)) checksFile = null;

            var closed = new List<string>();
            try
            {
                foreach (FileInfo f in new DirectoryInfo(handoffDirectory).EnumerateFiles("gate*_handoff.md").OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string title = Section(ReadText(f.FullName), "## 2.").Split('\n').FirstOrDefault() ?? string.Empty;
                    bool passed = (title.Contains("✅", StringComparison.Ordinal) || title.Contains("ALL PASS", StringComparison.OrdinalIgnoreCase) ||
                                   title.Contains("PASS", StringComparison.Ordinal)) &&
                                  !title.Contains("chưa", StringComparison.OrdinalIgnoreCase) && !title.Contains("⏳", StringComparison.Ordinal);
                    string name = Path.GetFileNameWithoutExtension(f.Name).Replace("_handoff", string.Empty, StringComparison.Ordinal);
                    if (passed && !string.Equals(f.Name, s.HandoffFile, StringComparison.OrdinalIgnoreCase))
                        closed.Add("Gate " + name.Substring("gate".Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                closed.Clear();
            }

            string gateTitle = $"Gate {(number ?? "?")} · {s.TaskId ?? "TASK-?"}";
            double ms = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
            return new AllianceBoard(s, gateTitle, checks, checksFile, gemini, claudeCode, closed, ms);
        }

        /// <summary>E1..E5 of a verification report: "E1_Name": {"pass": ..} (Gate 10+) or "results": {"E1": {"pass": ..}} (older).</summary>
        private static IReadOnlyList<AllianceGateCheck> ReadChecks(string? path)
        {
            var found = new bool?[5];
            try
            {
                if (path != null && File.Exists(path))
                {
                    using FileStream fs = File.OpenRead(path);
                    using JsonDocument doc = JsonDocument.Parse(fs);
                    JsonElement root = doc.RootElement;
                    JsonElement scope = root.TryGetProperty("results", out JsonElement r) && r.ValueKind == JsonValueKind.Object ? r : root;
                    foreach (JsonProperty prop in scope.EnumerateObject())
                    {
                        string n = prop.Name;
                        if (n.Length < 2 || n[0] != 'E' || n[1] < '1' || n[1] > '5' || (n.Length > 2 && n[2] != '_')) continue;
                        if (prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("pass", out JsonElement p) &&
                            p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            found[n[1] - '1'] = p.GetBoolean();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Array.Clear(found);
            }
            return Enumerable.Range(0, 5).Select(i => new AllianceGateCheck($"E{i + 1}", found[i])).ToList();
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : string.Empty; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
        }

        /// <summary>"TASK-16b" / "ADR-66": the prefix, digits, and (for tasks) one optional lower-case letter.</summary>
        public static string? FirstToken(string text, string prefix, bool allowSuffixLetter)
        {
            int at = 0;
            while ((at = text.IndexOf(prefix, at, StringComparison.Ordinal)) >= 0)
            {
                int i = at + prefix.Length, start = i;
                while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                if (i > start)
                {
                    if (allowSuffixLetter && i < text.Length && char.IsAsciiLetterLower(text[i]) &&
                        (i + 1 >= text.Length || !char.IsAsciiLetterOrDigit(text[i + 1]))) i++;
                    return text.Substring(at, i - at);
                }
                at = i;
            }
            return null;
        }

        private static string? FirstBranch(string text)
        {
            int at = text.IndexOf("claude/", StringComparison.Ordinal);
            if (at < 0) return null;
            int i = at;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '/' or '-' or '_' or '.')) i++;
            return text.Substring(at, i - at).TrimEnd('.');
        }

        private static string? ReadGitBranch(string repoRoot)
        {
            string head = ReadText(Path.Combine(repoRoot, ".git", "HEAD")).Trim();
            const string Ref = "ref: refs/heads/";
            return head.StartsWith(Ref, StringComparison.Ordinal) ? head.Substring(Ref.Length)
                : head.Length >= 7 ? "(detached " + head.Substring(0, 7) + ")" : null;
        }

        /// <summary>The text from a "## N." heading to the next "## " heading (heading line included).</summary>
        public static string Section(string markdown, string headingPrefix)
        {
            int start;
            if (markdown.StartsWith(headingPrefix, StringComparison.Ordinal))
            {
                start = 0;
            }
            else
            {
                int at = markdown.IndexOf("\n" + headingPrefix, StringComparison.Ordinal);
                if (at < 0) return string.Empty;
                start = at + 1;
            }
            int end = markdown.IndexOf("\n## ", start + headingPrefix.Length, StringComparison.Ordinal);
            return (end < 0 ? markdown.Substring(start) : markdown.Substring(start, end - start)).Trim();
        }

        public static string FirstCodeBlock(string section)
        {
            int open = section.IndexOf("```", StringComparison.Ordinal);
            if (open < 0) return string.Empty;
            int lineEnd = section.IndexOf('\n', open);
            if (lineEnd < 0) return string.Empty;
            int close = section.IndexOf("```", lineEnd, StringComparison.Ordinal);
            return (close < 0 ? section.Substring(lineEnd + 1) : section.Substring(lineEnd + 1, close - lineEnd - 1)).TrimEnd();
        }

        /// <summary>The "&gt; " lines of a section with the marker removed (a lone "&gt;" becomes an empty line).</summary>
        public static string BlockQuote(string section)
        {
            var lines = section.Split('\n')
                .Where(l => l.StartsWith('>'))
                .Select(l => l.Length > 1 && l[1] == ' ' ? l.Substring(2) : l.Substring(1));
            return string.Join("\n", lines).Trim();
        }

        private static (string? File, bool? Pass, string? At) LatestVerification(string handoffDirectory)
        {
            try
            {
                FileInfo? newest = new DirectoryInfo(handoffDirectory).EnumerateFiles("task*_verification.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest == null) return (null, null, null);
                using FileStream s = newest.OpenRead();
                using JsonDocument doc = JsonDocument.Parse(s);
                bool? pass = doc.RootElement.TryGetProperty("allPassed", out JsonElement p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? p.GetBoolean() : null;
                string? at = doc.RootElement.TryGetProperty("timestamp", out JsonElement t) ? t.ToString() : newest.LastWriteTimeUtc.ToString("o");
                return (newest.Name, pass, at);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                return (null, null, null);
            }
        }
    }
}
