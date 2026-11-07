using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NEXUSHomeHub.Core
{
    public enum ApiKeySource
    {
        Missing,
        Dpapi,
        Environment
    }

    /// <summary>Where the Gemini key comes from. <see cref="Key"/> must never be logged, stored or displayed.</summary>
    public sealed record ApiKeyResolution(ApiKeySource Source, string? Key, string? Error)
    {
        public string SourceLabel => Source switch
        {
            ApiKeySource.Dpapi => "DPAPI",
            ApiKeySource.Environment => "ENV",
            _ => "None"
        };

        public override string ToString() => $"ApiKeyResolution {{ Source = {SourceLabel}, Error = {Error ?? "-"} }}"; // never the key
    }

    /// <summary>
    /// TASK-07 §3.1: Gemini API key encrypted with Windows DPAPI (CurrentUser scope + fixed entropy) in
    /// 03_Local_Data_Vault/config/gemini_key.dpapi (git-ignored, ADR-24). Only Nhat Hoa's Windows account on this PC
    /// can decrypt it. Resolution order: DPAPI file -> GEMINI_API_KEY environment variable -> Missing (no exception).
    /// Zero-Secret-Leak: nothing here logs, stores or returns the key except <see cref="Resolve"/>.
    /// </summary>
    public sealed class DpapiSecretVault
    {
        public const string FileName = "gemini_key.dpapi";
        public const string EnvironmentVariable = "GEMINI_API_KEY";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NEXUS_HOME_HUB_DPAPI_V1");

        /// <summary>Google API keys look like "AIza" + 35 characters; used to keep a pasted key out of logs and dispatches.</summary>
        public static readonly Regex GoogleApiKeyPattern = new(@"AIza[0-9A-Za-z_\-]{20,}", RegexOptions.CultureInvariant); // ADR-29: interpreted (no dynamic IL)

        private readonly Func<string?> _readEnvironment;

        /// <param name="vaultPath">Full path of gemini_key.dpapi.</param>
        /// <param name="readEnvironment">Test hook; default reads GEMINI_API_KEY from the process environment.</param>
        public DpapiSecretVault(string vaultPath, Func<string?>? readEnvironment = null)
        {
            VaultPath = vaultPath;
            _readEnvironment = readEnvironment ?? (() => System.Environment.GetEnvironmentVariable(EnvironmentVariable));
        }

        public string VaultPath { get; }

        public bool HasStoredKey => File.Exists(VaultPath);

        /// <summary>Encrypts and saves the key. Returns null on success, else a message that never contains the key.</summary>
        public string? Store(string? rawKey)
        {
            string key = (rawKey ?? string.Empty).Trim();
            if (key.Length < 20 || key.Length > 256) return "Khóa phải dài 20..256 ký tự.";
            foreach (char ch in key)
            {
                if (char.IsWhiteSpace(ch) || char.IsControl(ch)) return "Khóa không được chứa khoảng trắng hay ký tự điều khiển.";
            }
            if (!OperatingSystem.IsWindows()) return "DPAPI chỉ có trên Windows.";

            byte[] plain = Encoding.UTF8.GetBytes(key);
            try
            {
                byte[] blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
                string temp = $"{VaultPath}.{System.Environment.ProcessId}.tmp";
                File.WriteAllBytes(temp, blob);
                File.Move(temp, VaultPath, overwrite: true);
                return null;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                return $"Không lưu được khóa ({ex.GetType().Name}).";
            }
            finally
            {
                Array.Clear(plain);
            }
        }

        /// <summary>Decrypts the stored key. False when there is no file or it cannot be decrypted (other user / other PC).</summary>
        public bool TryReadStored(out string? key, out string? error)
        {
            key = null;
            error = null;
            if (!File.Exists(VaultPath)) return false;
            if (!OperatingSystem.IsWindows())
            {
                error = "DPAPI chỉ có trên Windows.";
                return false;
            }

            byte[]? plain = null;
            try
            {
                byte[] blob = File.ReadAllBytes(VaultPath);
                plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                key = Encoding.UTF8.GetString(plain);
                return key.Length > 0;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                error = $"Không giải mã được {FileName} ({ex.GetType().Name}): file của tài khoản Windows / máy khác, hoặc đã hỏng.";
                return false;
            }
            finally
            {
                if (plain != null) Array.Clear(plain);
            }
        }

        /// <summary>DPAPI file first, then GEMINI_API_KEY, else Missing. Never throws.</summary>
        public ApiKeyResolution Resolve()
        {
            string? storedError = null;
            if (HasStoredKey)
            {
                if (TryReadStored(out string? stored, out storedError) && !string.IsNullOrWhiteSpace(stored))
                    return new ApiKeyResolution(ApiKeySource.Dpapi, stored, null);
            }
            string? env = _readEnvironment()?.Trim();
            if (!string.IsNullOrEmpty(env)) return new ApiKeyResolution(ApiKeySource.Environment, env, storedError);
            return new ApiKeyResolution(ApiKeySource.Missing, null, storedError);
        }

        /// <summary>Deletes gemini_key.dpapi (user command /apikey clear). Returns true if a file was removed.</summary>
        public bool Clear(out string? error)
        {
            error = null;
            try
            {
                if (!File.Exists(VaultPath)) return false;
                File.Delete(VaultPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>"AIza...9xQ (39 chars, Source: DPAPI)": 4 first + 3 last characters and the length only.</summary>
        public static string MaskApiKey(string? key, ApiKeySource source)
        {
            string label = source switch
            {
                ApiKeySource.Dpapi => "DPAPI",
                ApiKeySource.Environment => "ENV",
                _ => "None"
            };
            if (string.IsNullOrEmpty(key)) return $"(không có khóa, Source: {label})";
            string shown = key.Length >= 12 ? $"{key.Substring(0, 4)}...{key.Substring(key.Length - 3)}" : "****";
            return $"{shown} ({key.Length} chars, Source: {label})";
        }

        /// <summary>Replaces anything that looks like a Google API key with a mask (for echoes and logs).</summary>
        public static string Redact(string text) =>
            GoogleApiKeyPattern.Replace(text, m => MaskApiKey(m.Value, ApiKeySource.Missing).Split(' ')[0]);
    }
}
