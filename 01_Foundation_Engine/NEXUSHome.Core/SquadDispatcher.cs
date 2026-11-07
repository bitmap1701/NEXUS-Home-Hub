using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NEXUSHomeHub.Core
{
    public enum NexusMode
    {
        Micro,
        Sprint,
        Full
    }

    /// <summary>One of Nhật Hòa's five personal squads.</summary>
    public sealed record SquadDefinition(
        string SquadId,
        string Name,
        CoreAgentSeed Primary,
        string QaAgentId,
        string[] Keywords);

    public sealed record AgentRef(string Id, string Name, string Division, bool Available);

    /// <summary>Output of dispatch_agent_squad.</summary>
    public sealed record DispatchPackage(
        long DispatchId,
        string SquadId,
        string SquadName,
        string NexusMode,
        AgentRef PrimaryAgent,
        AgentRef QaAgent,
        string MatchedBy,
        IReadOnlyList<string> MatchedKeywords,
        string QaPolicy,
        IReadOnlyList<string> HardwareConstraints,
        string SystemInstruction,
        double ElapsedMs);

    public sealed record DispatchOutcome(DispatchPackage? Package, string? Error, DispatchTimings? Timings = null);

    /// <summary>TASK-08: where Dispatch spends its time (persona file read, SQLite INSERT, SystemInstruction build).</summary>
    public sealed record DispatchTimings(double PersonaMs, double InsertMs, double BuildMs);

    /// <summary>
    /// 2. Định tuyến Biệt đội (TASK-03 §A.2): chooses Squad + Primary Agent + QA Agent from an explicit
    /// agent id or from task keywords, logs the dispatch to SQLite WAL and returns a 6-part system instruction.
    /// </summary>
    public sealed class SquadDispatcher
    {
        public const string RealityCheckerId = "testing-reality-checker";
        private const int TaskSummaryMaxChars = 200;
        public const int TaskDescriptionMaxChars = 8000;

        private static readonly Regex TokenSplitter = new(@"[^\p{L}\p{N}#+.]+", RegexOptions.CultureInvariant); // ADR-29

        /// <summary>
        /// Hardware/environment constraints from GEMINI.md, injected into every dispatch.
        /// </summary>
        public static readonly IReadOnlyList<string> HardwareConstraints = new[]
        {
            "CPU: Intel Core i5-12400 (6C/12T); RAM: 16GB DDR4 dual-channel XMP",
            "Primary display: LG UltraGear 27G523B-B 200Hz on Galax GTX 1660, reserved for games and IDE (GPU load < 80%, frame time < 5 ms)",
            "Secondary display: MSI Optix G272 on Intel UHD 730, hosts the NEXUS Cockpit UI",
            "Input: AULA HERO 68 HE (65% layout, 8000Hz polling); separate mic (PCM 16kHz + VAD); in-ear headset",
            "Storage: SQLite WAL (synchronous=NORMAL, busy_timeout=5000) in 03_Local_Data_Vault; hot cache < 128MB",
            "Stack: C# .NET 8, raw/async TCP and UDP sockets, local MCP server (JSON-RPC stdio)"
        };

        /// <summary>
        /// The five squads. Agent ids are real The Agency slugs; see qa_report.md for the mapping from the
        /// names used in the TASK-03 spec. Keywords are matched on lowercase text, so both accented and
        /// unaccented Vietnamese spellings are listed.
        /// </summary>
        public static readonly IReadOnlyList<SquadDefinition> Squads = new[]
        {
            new SquadDefinition(
                "Squad1", "Core .NET, Socket & DB",
                new CoreAgentSeed("engineering-backend-architect", "Backend Architect",
                    "engineering/engineering-backend-architect.md", "Squad1 Core .NET, Socket & DB primary"),
                RealityCheckerId,
                new[]
                {
                    "c#", "csharp", ".net", "dotnet", "socket", "udp", "tcp", "sqlite", "wal", "database", "db",
                    "sql", "api", "backend", "server", "async", "packet", "gói tin", "goi tin", "mạng", "lập trình mạng",
                    "lap trinh mang", "rpc"
                }),
            new SquadDefinition(
                "Squad2", "UTH Academic & Jira",
                new CoreAgentSeed("product-sprint-prioritizer", "Sprint Prioritizer",
                    "specialized/product-sprint-prioritizer.md", "Squad2 UTH Academic & Jira primary"),
                RealityCheckerId,
                new[]
                {
                    "uth", "jira", "sprint", "backlog", "deadline", "assignment", "thesis", "môn", "môn học",
                    "mon hoc", "bài tập", "bai tap", "đồ án", "do an", "báo cáo", "bao cao", "học kỳ", "hoc ky",
                    "lịch học", "lich hoc", "thi cử", "kiểm tra", "kiem tra"
                }),
            new SquadDefinition(
                "Squad3", "Gaming & 200Hz Performance",
                new CoreAgentSeed("unreal-systems-engineer", "Unreal Systems Engineer",
                    "game-development/unreal-systems-engineer.md", "Squad3 Gaming & 200Hz Performance primary"),
                "testing-performance-benchmarker",
                new[]
                {
                    "game", "pubg", "delta force", "fc online", "fps", "200hz", "144hz", "frame", "frametime",
                    "frame time", "gtx", "gpu", "unreal", "unity", "godot", "input lag", "lag", "giật", "giat",
                    "khung hình", "khung hinh"
                }),
            new SquadDefinition(
                "Squad4", "Smart Room, MCP & Zettelkasten",
                new CoreAgentSeed("agents-orchestrator", "Agents Orchestrator",
                    "specialized/agents-orchestrator.md", "Squad4 Smart Room, MCP & Zettelkasten primary (default squad)"),
                RealityCheckerId,
                new[]
                {
                    "mcp", "zettelkasten", "smart room", "iot", "automation", "workflow", "orchestrator", "agent",
                    "agents", "ghi chú", "ghi chu", "note", "notes", "nhà thông minh", "nha thong minh", "phòng",
                    "đèn", "điều hòa", "dieu hoa", "tự động hóa", "tu dong hoa"
                }),
            new SquadDefinition(
                "Squad5", "Hardware Finance & Fitness",
                new CoreAgentSeed("testing-tool-evaluator", "Tool Evaluator",
                    "specialized/testing-tool-evaluator.md", "Squad5 Hardware Finance & Fitness primary"),
                RealityCheckerId,
                new[]
                {
                    "budget", "price", "fitness", "gym", "workout", "ssd", "giá", "mua", "ngân sách", "ngan sach",
                    "chi phí", "chi phi", "linh kiện", "linh kien", "nâng cấp", "nang cap", "tập luyện", "tap luyen",
                    "sức khỏe", "suc khoe", "calo", "cân nặng", "can nang"
                })
        };

        /// <summary>Default squad when no keyword matches: the orchestrator decides.</summary>
        public static SquadDefinition DefaultSquad => Squads[3];

        /// <summary>Core agents to seed into 02_Agents_Registry: the five primaries plus the Reality Checker.</summary>
        public static IReadOnlyList<CoreAgentSeed> CoreAgentSeeds =>
            Squads.Select(s => s.Primary)
                .Append(new CoreAgentSeed(RealityCheckerId, "Reality Checker",
                    "testing_security/testing-reality-checker.md", "QA gate for every squad"))
                .ToList();

        private readonly AgentRegistry _registry;
        private readonly SqliteMemoryStore _store;

        public SquadDispatcher(AgentRegistry registry, SqliteMemoryStore store)
        {
            _registry = registry;
            _store = store;
        }

        public static bool TryParseMode(string? value, out NexusMode mode)
        {
            mode = NexusMode.Micro;
            if (string.IsNullOrWhiteSpace(value)) return true;

            string normalized = value.Trim();
            if (normalized.StartsWith("NEXUS-", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring("NEXUS-".Length);
            return Enum.TryParse(normalized, ignoreCase: true, out mode) && Enum.IsDefined(mode);
        }

        public AgentRef DescribeAgent(string agentId)
        {
            return _registry.TryGet(agentId, out AgentProfile profile)
                ? new AgentRef(profile.Id, profile.Name, profile.Division, true)
                : new AgentRef(agentId, agentId, "missing", false);
        }

        /// <summary>Scores every squad by keyword hits; ties go to the lower squad number.</summary>
        public static (SquadDefinition Squad, IReadOnlyList<string> Matched) Route(string taskDescription)
        {
            string text = taskDescription.Normalize(NormalizationForm.FormC).ToLowerInvariant();
            string[] tokens = TokenSplitter.Split(text)
                .Select(t => t.TrimEnd('.'))
                .Where(t => t.Length > 0)
                .ToArray();
            var tokenSet = new HashSet<string>(tokens, StringComparer.Ordinal);
            string padded = " " + string.Join(' ', tokens) + " ";

            SquadDefinition best = DefaultSquad;
            List<string> bestMatches = new();

            foreach (SquadDefinition squad in Squads)
            {
                var matches = squad.Keywords
                    .Where(k => k.Contains(' ') ? padded.Contains(" " + k + " ", StringComparison.Ordinal) : tokenSet.Contains(k))
                    .ToList();
                if (matches.Count > bestMatches.Count)
                {
                    best = squad;
                    bestMatches = matches;
                }
            }

            return (best, bestMatches);
        }

        public DispatchOutcome Dispatch(string taskDescription, string? nexusMode, string? preferredAgentId)
        {
            var stopwatch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(taskDescription))
                return new DispatchOutcome(null, "taskDescription is required.");
            if (taskDescription.Length > TaskDescriptionMaxChars)
                return new DispatchOutcome(null, $"taskDescription exceeds {TaskDescriptionMaxChars} characters.");
            // TASK-07 Zero-Secret-Leak: a task containing an API key would be stored in agent_dispatch_logs.task_summary.
            if (DpapiSecretVault.GoogleApiKeyPattern.IsMatch(taskDescription))
                return new DispatchOutcome(null, "taskDescription contains what looks like an API key: not dispatched, not stored.");
            if (!TryParseMode(nexusMode, out NexusMode mode))
                return new DispatchOutcome(null, $"nexusMode '{nexusMode}' is invalid. Use Micro, Sprint or Full.");

            SquadDefinition squad;
            AgentProfile primary;
            string matchedBy;
            IReadOnlyList<string> matched;

            if (!string.IsNullOrWhiteSpace(preferredAgentId))
            {
                AgentLookup lookup = _registry.Resolve(preferredAgentId);
                if (lookup.Profile == null) return new DispatchOutcome(null, lookup.Error);

                primary = lookup.Profile;
                (SquadDefinition routed, IReadOnlyList<string> routedMatches) = Route(taskDescription);
                squad = Squads.FirstOrDefault(s => s.Primary.AgentId.Equals(primary.Id, StringComparison.OrdinalIgnoreCase)) ?? routed;
                matched = routedMatches;
                matchedBy = "PreferredAgentId";
            }
            else
            {
                (squad, matched) = Route(taskDescription);
                matchedBy = matched.Count > 0 ? "Keywords" : "DefaultSquad";
                if (!_registry.TryGet(squad.Primary.AgentId, out primary))
                {
                    return new DispatchOutcome(null,
                        $"Core agent '{squad.Primary.AgentId}' for {squad.SquadId} is missing from {string.Join(", ", _registry.Roots)}.");
                }
            }

            AgentRef qa = DescribeAgent(squad.QaAgentId);
            if (!qa.Available && !squad.QaAgentId.Equals(RealityCheckerId, StringComparison.OrdinalIgnoreCase))
            {
                qa = DescribeAgent(RealityCheckerId);
            }

            string persona;
            double tPersona = stopwatch.Elapsed.TotalMilliseconds;
            try
            {
                persona = _registry.LoadMarkdown(primary);
            }
            catch (Exception ex)
            {
                return new DispatchOutcome(null, $"Cannot read agent file for '{primary.Id}': {ex.Message}");
            }

            string summary = taskDescription.Trim();
            if (summary.Length > TaskSummaryMaxChars) summary = summary.Substring(0, TaskSummaryMaxChars) + "…";

            double tInsert = stopwatch.Elapsed.TotalMilliseconds;
            long dispatchId = _store.InsertDispatchLog(mode.ToString(), squad.SquadId, primary.Id, qa.Id, summary);
            double tBuild = stopwatch.Elapsed.TotalMilliseconds;
            string qaPolicy = QaPolicyFor(mode);
            var primaryRef = new AgentRef(primary.Id, primary.Name, primary.Division, true);
            string instruction = BuildSystemInstruction(dispatchId, squad, primaryRef, qa, mode, qaPolicy, taskDescription.Trim(), persona);

            stopwatch.Stop();
            double end = stopwatch.Elapsed.TotalMilliseconds;
            return new DispatchOutcome(new DispatchPackage(
                dispatchId, squad.SquadId, squad.Name, mode.ToString(), primaryRef, qa, matchedBy, matched,
                qaPolicy, HardwareConstraints, instruction, Math.Round(end, 3)), null,
                new DispatchTimings(Math.Round(tInsert - tPersona, 3), Math.Round(tBuild - tInsert, 3), Math.Round(end - tBuild, 3)));
        }

        /// <summary>
        /// TASK-08 ADR-28 warm-up (no INSERT): reads every squad's primary + QA persona file and builds one
        /// SystemInstruction, so the first real /ask pays neither the cold file reads nor the builder JIT.
        /// </summary>
        public int WarmUpPersonas()
        {
            int read = 0;
            foreach (SquadDefinition squad in Squads)
            {
                foreach (string id in new[] { squad.Primary.AgentId, squad.QaAgentId })
                {
                    if (!_registry.TryGet(id, out AgentProfile profile)) continue;
                    try
                    {
                        string persona = _registry.LoadMarkdown(profile);
                        read++;
                        if (id == squad.Primary.AgentId && read == 1)
                        {
                            var primaryRef = new AgentRef(profile.Id, profile.Name, profile.Division, true);
                            BuildSystemInstruction(0, squad, primaryRef, DescribeAgent(squad.QaAgentId), NexusMode.Micro,
                                QaPolicyFor(NexusMode.Micro), "warm up", persona);
                        }
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        // a missing persona is reported by the real dispatch
                    }
                }
            }
            return read;
        }

        /// <summary>
        /// TASK-09 ADR-36/37: the Primary Agent's 6-part SystemInstruction for an existing dispatch (/retry, /followup), rebuilt
        /// from the row (squad, primary / QA agent, mode, task_summary) and the persona file. No INSERT.
        /// </summary>
        public string? BuildInstructionFor(RecentDispatch row, out string? error)
        {
            error = null;
            if (!_registry.TryGet(row.PrimaryAgent, out AgentProfile primary))
            {
                error = $"Primary agent '{row.PrimaryAgent}' of dispatch #{row.Id} is missing from {string.Join(", ", _registry.Roots)}.";
                return null;
            }
            string persona;
            try
            {
                persona = _registry.LoadMarkdown(primary);
            }
            catch (Exception ex)
            {
                error = $"Cannot read agent file for '{primary.Id}': {ex.Message}";
                return null;
            }
            SquadDefinition squad = Squads.FirstOrDefault(s => s.SquadId == row.SquadId) ?? DefaultSquad;
            NexusMode mode = TryParseMode(row.NexusMode, out NexusMode parsed) ? parsed : NexusMode.Micro;
            var primaryRef = new AgentRef(primary.Id, primary.Name, primary.Division, true);
            return BuildSystemInstruction(row.Id, squad, primaryRef, DescribeAgent(row.QaAgent), mode, QaPolicyFor(mode), row.TaskSummary, persona);
        }

        public static string QaPolicyFor(NexusMode mode) => mode switch
        {
            NexusMode.Micro => "NEXUS-Micro: one specialist, one QA pass by the QA agent before delivery.",
            NexusMode.Sprint => "NEXUS-Sprint: Dev<->QA loop per task; maximum 3 retries, then escalate to Nhật Hòa.",
            _ => "NEXUS-Full: quality gate between every phase (0-6); Reality Checker signs off each gate with evidence."
        };

        /// <summary>
        /// 6-Part Agent Anatomy: 1 Identity, 2 Mission, 3 Critical Rules, 4 Hardware & Environment,
        /// 5 Deliverables & QA, 6 Workflow.
        /// </summary>
        private static string BuildSystemInstruction(
            long dispatchId, SquadDefinition squad, AgentRef primary, AgentRef qa, NexusMode mode,
            string qaPolicy, string task, string persona)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# NEXUS Dispatch #{dispatchId}: {squad.SquadId} ({squad.Name}), mode NEXUS-{mode}");
            sb.AppendLine();
            sb.AppendLine($"## 1. Identity: {primary.Name} ({primary.Id}, {primary.Division})");
            sb.AppendLine(persona.Trim());
            sb.AppendLine();
            sb.AppendLine("## 2. Mission");
            sb.AppendLine(task);
            sb.AppendLine();
            sb.AppendLine("## 3. Critical Rules (NEXUS Home Hub)");
            sb.AppendLine("- Minimal Change: smallest fix that works (bug patches < 30 lines); no luxury features; never rip out existing architecture.");
            sb.AppendLine("- 3-Tier Execution Gate: Green_SafeRead runs automatically; Yellow_ReversibleTune needs a Git/snapshot first; Red_CriticalSystem (delete files, registry, kill process, admin PowerShell) MUST stop for Nhật Hòa's confirmation.");
            sb.AppendLine("- 3-Strike Circuit Breaker: after 3 consecutive build/test failures, stop and report to 03_Local_Data_Vault/handoff/qa_report.md.");
            sb.AppendLine("- Secrets: never hard-code API keys; use Windows DPAPI, dotnet user-secrets or environment variables.");
            sb.AppendLine();
            sb.AppendLine("## 4. Hardware & Environment");
            foreach (string constraint in HardwareConstraints) sb.AppendLine($"- {constraint}");
            sb.AppendLine();
            sb.AppendLine("## 5. Deliverables & QA");
            sb.AppendLine($"- QA agent: {qa.Name} ({qa.Id}). {qaPolicy}");
            sb.AppendLine($"- Close the loop with MCP tool update_dispatch_verdict(dispatchId={dispatchId}, verdict=PASSED|NEEDS_WORK, evidenceNotes=...). PASSED requires evidence (build/test output, measurements).");
            sb.AppendLine();
            sb.AppendLine($"## 6. Workflow (NEXUS-{mode})");
            sb.AppendLine(mode switch
            {
                NexusMode.Micro => "Discover -> implement the single change -> self-check -> hand to QA agent.",
                NexusMode.Sprint => "Plan tasks -> for each task: implement -> QA review -> fix (max 3 retries) -> next task -> sprint report.",
                _ => "Phase 0 Discovery -> 1 Strategy -> 2 Foundation -> 3 Build -> 4 Hardening -> 5 Launch -> 6 Operate, with a QA gate between phases."
            });
            return sb.ToString();
        }
    }
}
