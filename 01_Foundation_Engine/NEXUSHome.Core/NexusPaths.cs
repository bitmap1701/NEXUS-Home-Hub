using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NEXUSHomeHub.Core
{
    /// <summary>
    /// Resolves NEXUS Home Hub folders without hard-coding a drive letter, so the same build works from
    /// C:\Agent\NEXUS_Home_Hub or D:\NEXUS_Home_Hub.
    /// Order: env NEXUS_HOME_ROOT -> walk up from the executable until 01_Foundation_Engine + 03_Local_Data_Vault
    /// are found -> legacy C:\Agent\NEXUS_Home_Hub. NEXUS_VAULT_PATH / NEXUS_AGENTS_PATH still override as before.
    /// </summary>
    public static class NexusPaths
    {
        public const string LegacyRepoRoot = @"C:\Agent\NEXUS_Home_Hub";
        public const string LegacySkillsLibrary = @"C:\Agent\.agents\skills";
        public const string AgentsRegistryFolder = "02_Agents_Registry";

        public static string ResolveRepoRoot()
        {
            string? env = Environment.GetEnvironmentVariable("NEXUS_HOME_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "01_Foundation_Engine")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "03_Local_Data_Vault")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }

            return LegacyRepoRoot;
        }

        public static string ResolveVaultPath() =>
            Environment.GetEnvironmentVariable("NEXUS_VAULT_PATH")
            ?? Path.Combine(ResolveRepoRoot(), "03_Local_Data_Vault");

        /// <summary>02_Agents_Registry inside the repo (TASK-03 primary root, also where core agents are seeded).</summary>
        public static string ResolveAgentsRegistryPath() =>
            Path.Combine(ResolveRepoRoot(), AgentsRegistryFolder);

        /// <summary>
        /// Agent roots in priority order: 02_Agents_Registry first, then the installed The Agency skills library
        /// (NEXUS_AGENTS_PATH, default C:\Agent\.agents\skills). Duplicate ids resolve to the first root.
        /// </summary>
        public static IReadOnlyList<string> ResolveAgentRoots()
        {
            string library = Environment.GetEnvironmentVariable("NEXUS_AGENTS_PATH") ?? LegacySkillsLibrary;
            return new[] { ResolveAgentsRegistryPath(), library }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFullPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
