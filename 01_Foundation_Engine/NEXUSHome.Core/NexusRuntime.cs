using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// TASK-06 ADR-21: one composition root for HostService (Cockpit) and McpTools, so both processes resolve the same
    /// paths, load the same nexus_config.json, open SQLite the same way (WAL + busy_timeout + migration) and share the
    /// same dispatcher / verdict state machine. Before TASK-06 each Program.cs wired these up by hand.
    /// </summary>
    public sealed class NexusRuntime
    {
        private NexusRuntime(string source, string vaultPath)
        {
            Source = source;
            VaultPath = vaultPath;
            HandoffDirectory = Path.Combine(vaultPath, "handoff");
            Directory.CreateDirectory(HandoffDirectory);

            // Config first: it configures GameBoost (protected prefixes, trim floor) for this process.
            Config = new NexusConfigService(Path.Combine(vaultPath, "config", NexusConfigService.FileName));

            DatabasePath = Path.Combine(vaultPath, "structured_db", "nexus_memory.db");
            Store = new SqliteMemoryStore(DatabasePath);
            Registry = new AgentRegistry(NexusPaths.ResolveAgentRoots());
            Dispatcher = new SquadDispatcher(Registry, Store);
            Verdicts = new DispatchVerdictService(Store, Registry, HandoffDirectory, source);

            // TASK-07 / TASK-10 / TASK-11: DPAPI key vault, QuotaGuard, ResponseCache (ADR-44), Grounding (ADR-45), and Consult.
            Vault = new DpapiSecretVault(Path.Combine(vaultPath, "config", DpapiSecretVault.FileName));
            QuotaGuard = new GeminiQuotaGuard();
            QuotaGuard.SeedFromStore(Store);
            ResponseCache = new GeminiResponseCache(Store);
            CacheMaintenance = ResponseCache.PurgeExpired(); // TASK-16c (decision 3.2b): expired cache rows go, light WAL checkpoint
            Grounding = new VaultGroundingEngine(vaultPath);
            Consult = new AgentConsultService(this, Vault, null, QuotaGuard);

            // TASK-14 ADR-59: /calc, /timer, /note (0 tokens). /note invalidates the grounding memo of this process.
            LocalUtilities = new LocalUtilityService(LatestDispatchPath, vaultPath, source, Store, Grounding);

            // TASK-13: Right MFD pins (config/mfd_pins.json) and the cards of the current response.
            string configDirectory = Path.GetDirectoryName(Config.ConfigPath) ?? Path.Combine(vaultPath, "config");
            Pins = new MfdPinManager(configDirectory, () => Config.Current.MaxPinnedCards);
            HudWidgets = new HudWidgetService(LatestDispatchPath, Pins);
            MfdPinManager pins = Pins;
            LatestDispatchFile.PinCountProvider = () => pins.Count;
            // TASK-16 U3: wrist alert on /timer expiry (this process's timers) and on a 3-strike escalation (DevQaLoop).
            WatchWebhook.ConfigProvider = () => Config.Current;
            LocalUtilities.TimerExpired += (_, snapshot, _) => _ = WatchWebhook.Notify(WatchWebhook.TimerEvent, snapshot.AiResponse);
            // TASK-16 U2: created here, started only by the HostService Cockpit (Configure); McpTools / selftests never start it.
            YouTube = new YouTubeG272Watcher(Config);
            UiLayout = new CockpitUiStore(configDirectory);
            ConfigDirectory = configDirectory;

            // TASK-12 ADR-61: /img, /snap, MCP analyze_visual_input (Vision SHA-256 cache in gemini_response_cache).
            Vision = new VisionConsultService(this);

            // TASK-17 ADR-67: /net radar (on demand only) and the microphone (opened only for an explicit /ptt or F1 session).
            Network = new NetworkRadar();
            // TASK-17a ADR-69: nexus_config.json "networkTargets" read at every scan (so /reload applies at once); null = 4 defaults
            NexusConfigService config = Config;
            Network.TargetsProvider = () => config.Current.NetworkTargets?.Select(t => new NetworkRadar.Target(t.Label, t.Host)).ToList();
            Mic = new MicService(UiLayout, new LocalSttService(() => config.Current.Stt)); // TASK-18 / 18a: lan / cli / SAPI worker STT, "stt" read at every session (/reload)
        }

        /// <summary>TASK-17 N1: /net, the right-column NITRO5 line, the Alt+Q network page and MCP get_network_radar.</summary>
        public NetworkRadar Network { get; }

        /// <summary>TASK-17 V1: /mic, /ptt and the F1 switch "3. Mic 16kHz VAD".</summary>
        public MicService Mic { get; }

        /// <param name="source">"CockpitUI" (HostService) or "McpTools": written into latest_dispatch.json and audit rows.</param>
        /// <param name="vaultPath">Null = <see cref="NexusPaths.ResolveVaultPath"/>.</param>
        public static NexusRuntime Create(string source, string? vaultPath = null) =>
            new(source, vaultPath ?? NexusPaths.ResolveVaultPath());

        public string Source { get; }

        public string VaultPath { get; }

        public string HandoffDirectory { get; }

        /// <summary>03_Local_Data_Vault/config (TASK-16b: shift_state.json next to cockpit_ui.json).</summary>
        public string ConfigDirectory { get; }

        public string DatabasePath { get; }

        /// <summary>TASK-16c: the start-up purge of expired cache rows + PASSIVE WAL checkpoint of this process.</summary>
        public CacheMaintenanceResult CacheMaintenance { get; }

        public string LatestDispatchPath => Path.Combine(HandoffDirectory, LatestDispatchFile.FileName);

        public NexusConfigService Config { get; }

        public SqliteMemoryStore Store { get; }

        public AgentRegistry Registry { get; }

        public SquadDispatcher Dispatcher { get; }

        public DispatchVerdictService Verdicts { get; }

        public DpapiSecretVault Vault { get; }

        public GeminiQuotaGuard QuotaGuard { get; }

        public GeminiResponseCache ResponseCache { get; }

        public VaultGroundingEngine Grounding { get; }

        public AgentConsultService Consult { get; }

        /// <summary>TASK-14 ADR-59: Zero-Token Local Utilities (/calc, /timer, /note, MCP run_local_utility).</summary>
        public LocalUtilityService LocalUtilities { get; }

        /// <summary>TASK-13: Right MFD pin board (mfd_pins.json).</summary>
        public MfdPinManager Pins { get; }

        /// <summary>TASK-13: cards of the current response (/copy, /pin, Alt+C, Alt+P, MCP manage_hud_widgets).</summary>
        public HudWidgetService HudWidgets { get; }

        /// <summary>TASK-16 U2: YouTube on the G272 → Equalizer APO K450 profile switch (timer off until Configure()).</summary>
        public YouTubeG272Watcher YouTube { get; }

        /// <summary>TASK-16 U1: config/cockpit_ui.json (height level, position, auto-collapse, click-through).</summary>
        public CockpitUiStore UiLayout { get; }

        /// <summary>TASK-12 ADR-61: Multimodal Vision Engine (/img, /snap, MCP analyze_visual_input).</summary>
        public VisionConsultService Vision { get; }

        /// <summary>
        /// TASK-08 ADR-28: pays the one-time costs of the /ask synchronous path off the hot path (HostService runs it on a
        /// background thread at startup): pooled SQLite connection with its PRAGMAs plus the first write (one "host_warmup"
        /// audit row), routing + key-guard regex JIT, the squads' persona files + SystemInstruction builder, and the
        /// System.Text.Json metadata of latest_dispatch.json. Never touches DPAPI or the HTTP stack (ADR-28/29); no dispatch row.
        /// </summary>
        /// <summary>TASK-17b (17b.4): JIT-compiles the cold-path methods listed in <see cref="ColdPathNames"/> without running them.</summary>
        internal static int PrepareColdPaths()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            int prepared = 0;
            foreach (Type t in new[] { typeof(CockpitCommandProcessor), typeof(AgentConsultService), typeof(GeminiQuotaGuard) })
            {
                foreach (Type owner in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Prepend(t))
                {
                    if (owner.ContainsGenericParameters) continue;
                    bool ownerMatches = ColdPathNames.Names.Any(n => owner.Name.Contains("<" + n + ">", StringComparison.Ordinal));
                    foreach (MethodInfo m in owner.GetMethods(All))
                    {
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        if (!ownerMatches && !ColdPathNames.Names.Any(n => m.Name == n || m.Name.Contains("<" + n + ">", StringComparison.Ordinal))) continue;
                        RuntimeHelpers.PrepareMethod(m.MethodHandle);
                        prepared++;
                    }
                }
            }
            return prepared;
        }

        public WarmUpReport WarmUp()
        {
            var total = Stopwatch.StartNew();
            var step = Stopwatch.StartNew();
            double sqliteMs = 0, routeMs = 0, jsonMs = 0;
            int personas = 0;
            string? error = null;
            try
            {
                Store.CountDispatchLogs();
                Store.GetLatestDispatchId();
                QuotaGuard.SeedFromStore(Store);
                ResponseCache.GetStats();
                // First write of the process (write lock, WAL append, commit) paid here, as one honest audit row.
                Store.WriteAuditLog("host_warmup", ActionRiskLevel.Green_SafeRead, $"{{\"source\":\"{Source}\"}}", "WarmUp");
                sqliteMs = step.Elapsed.TotalMilliseconds;

                step.Restart();
                SquadDispatcher.Route("warm up C# UDP socket latency");
                personas = Dispatcher.WarmUpPersonas();
                DpapiSecretVault.GoogleApiKeyPattern.IsMatch("warm up");
                SquadDispatcher.TryParseMode(Config.Current.DefaultNexusMode, out _);
                Grounding.Search("ADR-44", 1, 300);
                LocalCalculator.Evaluate("sqrt(144) + 2^8 + (0xFF | 0b101) << 1"); // TASK-14: JIT the /calc parser off the hot path
                LocalTimerManager.TryParseDuration("1h30m", out _, out _);
                LocalNoteService.ContainsSecret("warm up");

                // Warm up the full ADR-44 cache-hit code paths (JIT + SQLite prepared statements) with rollback so 0 rows remain.
                const string WarmTask = "__nexus_warmup_cache_task__";
                string[] warmSources = new[] { "[warmup#1]" };
                string warmModelId = GeminiResponseCache.ResolveModelIdForCache(Config.Current, isQa: false);
                string warmKey = GeminiResponseCache.ComputeKey(warmModelId, WarmTask, "warmup_fp");
                ResponseCache.Put(warmKey, "ground", warmModelId, WarmTask, "warmup_fp", warmSources, "warmup response", 10, 10);
                Consult.TryExecuteCachedConsult(WarmTask, "Micro", null, "ground", "warmup_fp", warmSources,
                    out _, out ConsultOutcome? warmOutcome, out _, out _, rollbackWarmup: true);
                if (warmOutcome?.Row != null)
                {
                    string qaModelId = GeminiResponseCache.ResolveModelIdForCache(Config.Current, isQa: true);
                    string qaPrompt = Consult.BuildQaContent(warmOutcome.Row, string.Empty);
                    string qaKey = GeminiResponseCache.ComputeKey(qaModelId, qaPrompt, string.Empty);
                    ResponseCache.Put(qaKey, "qa", qaModelId, qaPrompt, string.Empty, Array.Empty<string>(), "VERDICT: PASSED\nWarmup", 10, 10);
                    Consult.TryExecuteCachedQa(warmOutcome.Row, string.Empty, out _, rollbackWarmup: true);
                    ResponseCache.DeleteKey(qaKey);
                }
                ResponseCache.DeleteKey(warmKey);
                // TASK-17b ADR-70 (17b.4): with TieredCompilation off each method is JIT-compiled fully optimized on its first
                // call (Gate 17b run 1/3: the first /qa took 14 ms, then 0,4 ms). JIT the /qa and /retry synchronous paths here
                // and run the read-only part of /qa once: no quota slot, no Gemini call, no write (rollbackWarmup).
                PrepareColdPaths();
                QaStart warmQa = Consult.StartQa(null, "warm up", writeFile: false);
                if (warmQa.Row != null) Consult.TryExecuteCachedQa(warmQa.Row, "warm up", out _, rollbackWarmup: true);
                routeMs = step.Elapsed.TotalMilliseconds;

                step.Restart();
                var dummy = new LatestDispatchSnapshot("warmup", Source, 0, "DISPATCHED", 0, string.Empty, string.Empty, string.Empty,
                    string.Empty, "Micro", "Squad4", null, new AgentName("-", "-"), new AgentName("-", "-"),
                    SquadDispatcher.RealityCheckerId, Array.Empty<string>(), 0, string.Empty);
                LatestDispatchFile.Serialize(dummy);
                jsonMs = step.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }
            return new WarmUpReport(Math.Round(total.Elapsed.TotalMilliseconds, 2), Math.Round(sqliteMs, 2), Math.Round(routeMs, 2),
                Math.Round(jsonMs, 2), error, personas);
        }
    }

    internal static class ColdPathNames
    {
        /// <summary>Methods (and their lambdas / async state machines) on the /qa, /retry and /followup synchronous paths.</summary>
        public static readonly string[] Names =
            { "ExecuteQa", "ExecuteRefine", "SplitHashId", "StartQa", "TryExecuteCachedQa", "RunQaReviewAsync", "CompleteRetryAsync", "CompleteFollowUpAsync", "TryAcquireRequest" };
    }

    /// <remarks>SqliteMs includes one audit-row write; RouteMs includes reading the squads' persona files.</remarks>
    public sealed record WarmUpReport(double TotalMs, double SqliteMs, double RouteMs, double JsonMs, string? Error, int PersonasRead = 0);
}
