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
    }
}
