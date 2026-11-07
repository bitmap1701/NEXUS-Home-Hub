using System;
using System.Diagnostics;
using System.IO;

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

            // TASK-07: DPAPI key vault (git-ignored config folder, ADR-24) and the shared /ask + MCP consult flow.
            Vault = new DpapiSecretVault(Path.Combine(vaultPath, "config", DpapiSecretVault.FileName));
            Consult = new AgentConsultService(this);
        }

        /// <param name="source">"CockpitUI" (HostService) or "McpTools": written into latest_dispatch.json and audit rows.</param>
        /// <param name="vaultPath">Null = <see cref="NexusPaths.ResolveVaultPath"/>.</param>
        public static NexusRuntime Create(string source, string? vaultPath = null) =>
            new(source, vaultPath ?? NexusPaths.ResolveVaultPath());

        public string Source { get; }

        public string VaultPath { get; }

        public string HandoffDirectory { get; }

        public string DatabasePath { get; }

        public string LatestDispatchPath => Path.Combine(HandoffDirectory, LatestDispatchFile.FileName);

        public NexusConfigService Config { get; }

        public SqliteMemoryStore Store { get; }

        public AgentRegistry Registry { get; }

        public SquadDispatcher Dispatcher { get; }

        public DispatchVerdictService Verdicts { get; }

        public DpapiSecretVault Vault { get; }

        public AgentConsultService Consult { get; }

        /// <summary>
        /// TASK-08 ADR-28: pays the one-time costs of the /ask synchronous path off the hot path (HostService runs it on a
        /// background thread at startup): pooled SQLite connection with its PRAGMAs plus the first write (one "host_warmup"
        /// audit row), routing + key-guard regex JIT, the squads' persona files + SystemInstruction builder, and the
        /// System.Text.Json metadata of latest_dispatch.json. Never touches DPAPI or the HTTP stack (ADR-28/29); no dispatch row.
        /// </summary>
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
                // First write of the process (write lock, WAL append, commit) paid here, as one honest audit row.
                Store.WriteAuditLog("host_warmup", ActionRiskLevel.Green_SafeRead, $"{{\"source\":\"{Source}\"}}", "WarmUp");
                sqliteMs = step.Elapsed.TotalMilliseconds;

                step.Restart();
                SquadDispatcher.Route("warm up C# UDP socket latency");
                personas = Dispatcher.WarmUpPersonas();
                DpapiSecretVault.GoogleApiKeyPattern.IsMatch("warm up");
                SquadDispatcher.TryParseMode(Config.Current.DefaultNexusMode, out _);
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

    /// <remarks>SqliteMs includes one audit-row write; RouteMs includes reading the squads' persona files.</remarks>
    public sealed record WarmUpReport(double TotalMs, double SqliteMs, double RouteMs, double JsonMs, string? Error, int PersonasRead = 0);
}
