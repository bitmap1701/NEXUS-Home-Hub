using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NEXUSHomeHub.Core
{
    /// <summary>Lightweight metadata for one agent file (the Markdown body is loaded only on dispatch).</summary>
    public sealed record AgentProfile(
        string Id,
        string Name,
        string Division,
        string Summary,
        string FullPath,
        string RootPath,
        bool IsPlaceholder = false);

    /// <summary>Result of resolving an agent id or relative path.</summary>
    public sealed record AgentLookup(AgentProfile? Profile, string? Error)
    {
        public bool Found => Profile != null;
    }

    /// <summary>A core agent that must exist in 02_Agents_Registry (TASK-03 §A.1).</summary>
    public sealed record CoreAgentSeed(string AgentId, string DisplayName, string RelativePath, string Role);

    /// <summary>
    /// 1. Quản lý và nạp động hồ sơ từ kho 283 Agents (.md) trên ổ cứng.
    /// Indexes metadata only (id, name, division, summary) from every *.md with YAML frontmatter under the
    /// configured roots. Supports both layouts:
    ///   - &lt;root&gt;/&lt;division&gt;/&lt;slug&gt;.md          (02_Agents_Registry, The Agency repo layout)
    ///   - &lt;root&gt;/agency-&lt;slug&gt;/SKILL.md         (installed skills library, e.g. C:\Agent\.agents\skills)
    /// </summary>
    public class AgentRegistry
    {
        private const int HeaderReadChars = 4096;
        private const int SummaryMaxChars = 200;

        private static readonly string[] KnownDivisions =
        {
            "academic", "design", "engineering", "finance", "game-development", "gis", "healthcare",
            "marketing", "paid-media", "product", "project-management", "project-manager", "research",
            "sales", "security", "spatial-computing", "specialized", "support", "testing"
        };

        /// <summary>Top-level folders that never hold source agents (mirrors The Agency NON_DIVISION_DIRS).</summary>
        private static readonly HashSet<string> NonAgentFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "integrations", "scripts", "examples", "strategy", ".git", ".github"
        };

        private readonly List<string> _roots;
        private readonly object _gate = new();
        private AgentIndex? _index;

        /// <summary>Profiles (one per agent) plus a lookup keyed by id and aliases.</summary>
        private sealed record AgentIndex(List<AgentProfile> Profiles, Dictionary<string, AgentProfile> Lookup);

        public AgentRegistry(string agentsRootPath)
            : this(new[] { agentsRootPath })
        {
        }

        public AgentRegistry(IEnumerable<string> agentRoots)
        {
            _roots = agentRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => Path.GetFullPath(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public IReadOnlyList<string> Roots => _roots;

        /// <summary>First root (02_Agents_Registry): the only place seeding ever writes to.</summary>
        public string PrimaryRoot => _roots.Count > 0 ? _roots[0] : string.Empty;

        public int Count => EnsureIndex().Profiles.Count;

        public IReadOnlyList<AgentProfile> GetAll() =>
            EnsureIndex().Profiles
                .OrderBy(a => a.Division, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

        public IReadOnlyList<string> ListAvailableAgents(int maxCount = 50) =>
            GetAll().Take(Math.Max(0, maxCount)).Select(a => a.Id).ToList();

        /// <summary>
        /// Looks up an agent by id or alias. "engineering-backend-architect" (repo file stem),
        /// "backend-architect" (installed skill folder agency-backend-architect) and the name slug all resolve
        /// to the same agent, because the skills library names folders after the agent name, not the file.
        /// </summary>
        public bool TryGet(string agentId, out AgentProfile profile)
        {
            Dictionary<string, AgentProfile> lookup = EnsureIndex().Lookup;
            if (lookup.TryGetValue(agentId, out AgentProfile? found) ||
                lookup.TryGetValue(StripDivisionPrefix(agentId), out found))
            {
                profile = found!;
                return true;
            }
            profile = null!;
            return false;
        }

        /// <summary>Drops the cached index (next call rescans the roots).</summary>
        public void Reload()
        {
            lock (_gate)
            {
                _index = null;
            }
        }

        /// <summary>
        /// Resolves an indexed agent id ("engineering-backend-architect") or a relative .md path inside a root
        /// ("engineering/engineering-backend-architect.md"). Anything that escapes the roots after
        /// Path.GetFullPath (e.g. "../../GEMINI.md", absolute paths) is rejected.
        /// </summary>
        public AgentLookup Resolve(string agentIdOrRelativePath)
        {
            if (string.IsNullOrWhiteSpace(agentIdOrRelativePath))
                return new AgentLookup(null, "Agent id is empty.");

            string value = agentIdOrRelativePath.Trim();
            bool looksLikePath =
                value.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                value.Contains("..", StringComparison.Ordinal) ||
                value.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(value);

            if (!looksLikePath)
            {
                return TryGet(value, out AgentProfile byId)
                    ? new AgentLookup(byId, null)
                    : new AgentLookup(null, $"Agent '{value}' not found in registry.");
            }

            foreach (string root in _roots)
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(Path.Combine(root, value));
                }
                catch (Exception)
                {
                    continue;
                }

                if (!IsInsideRoot(root, fullPath)) continue;
                if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) continue;

                AgentProfile? profile = TryReadProfile(root, fullPath);
                if (profile != null) return new AgentLookup(profile, null);
            }

            return new AgentLookup(null,
                $"Rejected '{value}': only agent .md files inside the registry roots may be loaded (path traversal blocked).");
        }

        public static bool IsInsideRoot(string root, string fullPath)
        {
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        public string LoadMarkdown(AgentProfile profile) => File.ReadAllText(profile.FullPath, Encoding.UTF8);

        /// <summary>Phase 1 API, now routed through the traversal-safe resolver.</summary>
        public string LoadAgentSystemInstruction(string agentNameOrRelativePath)
        {
            AgentLookup lookup = Resolve(agentNameOrRelativePath);
            if (lookup.Profile == null)
            {
                return "You are the NEXUS Agents Orchestrator assisting Huynh Nhat Hoa in real-time on his local PC.";
            }

            string markdownPersona = LoadMarkdown(lookup.Profile);
            return $"{markdownPersona}\n\n[RUNTIME CONTEXT]: Running locally inside NEXUS Home Hub on i5-12400 + GTX 1660 + 16GB RAM. Use registered local MCP tools to inspect or tune the system safely.";
        }

        /// <summary>
        /// Ensures each core agent exists in 02_Agents_Registry. Copies the real file from another root when the
        /// agent is already installed (e.g. the The Agency skills library); otherwise writes a clearly marked
        /// placeholder so dispatch keeps working. Never overwrites an existing file.
        /// </summary>
        public IReadOnlyList<string> SeedCoreAgents(IEnumerable<CoreAgentSeed> seeds)
        {
            var actions = new List<string>();
            if (string.IsNullOrEmpty(PrimaryRoot)) return actions;

            foreach (CoreAgentSeed seed in seeds)
            {
                string target = Path.GetFullPath(Path.Combine(PrimaryRoot, seed.RelativePath));
                if (!IsInsideRoot(PrimaryRoot, target) || File.Exists(target)) continue;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (TryGet(seed.AgentId, out AgentProfile existing) && File.Exists(existing.FullPath))
                    {
                        File.Copy(existing.FullPath, target, overwrite: false);
                        actions.Add($"Copied {seed.AgentId} from {existing.FullPath}");
                    }
                    else
                    {
                        File.WriteAllText(target, BuildPlaceholder(seed), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                        actions.Add($"Placeholder {seed.AgentId} (source not installed)");
                    }
                }
                catch (Exception ex)
                {
                    actions.Add($"FAILED {seed.AgentId}: {ex.Message}");
                }
            }

            if (actions.Count > 0) Reload();
            return actions;
        }

        private static string BuildPlaceholder(CoreAgentSeed seed) =>
            "---\n" +
            $"name: {seed.DisplayName}\n" +
            $"description: Placeholder for {seed.Role}. Replace with The Agency source file {seed.AgentId}.md\n" +
            "seeded: placeholder\n" +
            "---\n\n" +
            $"# {seed.DisplayName}\n\n" +
            $"Seeded by NEXUS Home Hub because `{seed.AgentId}` was not found in any agent root.\n" +
            $"Role in NEXUS: {seed.Role}.\n\n" +
            "Install The Agency agents (C:\\Agent\\.agents\\skills) and delete this file to load the full persona.\n";

        private AgentIndex EnsureIndex()
        {
            AgentIndex? current = _index;
            if (current != null) return current;

            lock (_gate)
            {
                if (_index != null) return _index;

                var map = new Dictionary<string, AgentProfile>(StringComparer.OrdinalIgnoreCase);
                var profiles = new List<AgentProfile>();
                var seenAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var placeholders = new List<AgentProfile>();
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 4
                };

                foreach (string root in _roots)
                {
                    if (!Directory.Exists(root)) continue;

                    IEnumerable<string> files;
                    try
                    {
                        files = Directory.EnumerateFiles(root, "*.md", options);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (string file in files)
                    {
                        string firstSegment = Path.GetRelativePath(root, file)
                            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })[0];
                        if (NonAgentFolders.Contains(firstSegment)) continue;

                        AgentProfile? profile = TryReadProfile(root, file);
                        if (profile == null || map.ContainsKey(profile.Id)) continue;
                        if (profile.IsPlaceholder)
                        {
                            placeholders.Add(profile);
                            continue;
                        }

                        // A seeded copy in 02_Agents_Registry and the same agent in the skills library are one agent
                        // (same name slug); the earlier root wins. Do NOT strip division prefixes here:
                        // "Healthcare Customer Service" and "Customer Service" are different agents.
                        if (!seenAgents.Add(Slugify(profile.Name))) continue;

                        map[profile.Id] = profile;
                        profiles.Add(profile);
                    }
                }

                // Seeded placeholders only fill gaps: a real persona installed later always wins.
                foreach (AgentProfile placeholder in placeholders)
                {
                    if (map.ContainsKey(placeholder.Id) || !seenAgents.Add(Slugify(placeholder.Name))) continue;
                    map[placeholder.Id] = placeholder;
                    profiles.Add(placeholder);
                }

                // Aliases never shadow a real id; first agent wins on alias collisions.
                foreach (AgentProfile profile in profiles)
                {
                    foreach (string alias in new[] { StripDivisionPrefix(profile.Id), StripDivisionPrefix(Slugify(profile.Name)) })
                    {
                        if (alias.Length > 0) map.TryAdd(alias, profile);
                    }
                }

                _index = new AgentIndex(profiles, map);
                return _index;
            }
        }

        private static string StripDivisionPrefix(string id)
        {
            if (id.StartsWith("agency-", StringComparison.OrdinalIgnoreCase)) id = id.Substring("agency-".Length);
            foreach (string division in KnownDivisions)
            {
                string prefix = division + "-";
                if (id.Length > prefix.Length && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return id.Substring(prefix.Length);
            }
            return id;
        }

        /// <summary>Same rule as The Agency scripts/lib.sh slugify (lowercase, non [a-z0-9] -> '-').</summary>
        private static string Slugify(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.ToLowerInvariant())
            {
                bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (keep) sb.Append(c);
                else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        /// <summary>"agency-backend-architect" (skill frontmatter) -> "Backend Architect".</summary>
        private static string DisplayNameFrom(string name)
        {
            if (!name.StartsWith("agency-", StringComparison.OrdinalIgnoreCase)) return name;
            string[] words = name.Substring("agency-".Length).Split('-', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        private static AgentProfile? TryReadProfile(string root, string file)
        {
            string header;
            try
            {
                using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[HeaderReadChars];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                header = new string(buffer, 0, read).Replace("\r\n", "\n");
            }
            catch (Exception)
            {
                return null;
            }

            if (!header.StartsWith("---", StringComparison.Ordinal)) return null;
            int end = header.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return null;

            string frontmatter = header.Substring(3, end - 3);
            string? name = FrontmatterValue(frontmatter, "name");
            if (string.IsNullOrWhiteSpace(name)) return null;

            string description = FrontmatterValue(frontmatter, "description") ?? string.Empty;
            if (description.Length > SummaryMaxChars) description = description.Substring(0, SummaryMaxChars) + "…";

            string fileStem = Path.GetFileNameWithoutExtension(file);
            bool isSkillLayout = fileStem.Equals("SKILL", StringComparison.OrdinalIgnoreCase);
            string id = isSkillLayout ? Path.GetFileName(Path.GetDirectoryName(file)) ?? fileStem : fileStem;
            if (id.StartsWith("agency-", StringComparison.OrdinalIgnoreCase)) id = id.Substring("agency-".Length);

            string relative = Path.GetRelativePath(root, file);
            string[] segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            string division = !isSkillLayout && segments.Length > 1 ? segments[0] : InferDivision(id);

            bool isPlaceholder = string.Equals(FrontmatterValue(frontmatter, "seeded"), "placeholder", StringComparison.OrdinalIgnoreCase);
            return new AgentProfile(id, DisplayNameFrom(name.Trim()), division, description, file, root, isPlaceholder);
        }

        private static string InferDivision(string id)
        {
            foreach (string division in KnownDivisions)
            {
                if (id.StartsWith(division + "-", StringComparison.OrdinalIgnoreCase))
                    return division == "project-manager" ? "project-management" : division;
            }
            return "unclassified";
        }

        private static string? FrontmatterValue(string frontmatter, string key)
        {
            foreach (string rawLine in frontmatter.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) continue;

                string value = line.Substring(key.Length + 1).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                {
                    value = value.Substring(1, value.Length - 2);
                }
                return value;
            }
            return null;
        }
    }
}
