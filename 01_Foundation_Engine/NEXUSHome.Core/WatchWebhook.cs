using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-16 U3: one webhook attempt. <see cref="Host"/> only, never the full URL (it may carry a token).</summary>
    public sealed record WatchWebhookResult(
        string Event,
        bool Ok,
        bool Skipped,
        string? SkipReason,
        int? StatusCode,
        string? Error,
        string? Host,
        double ElapsedMs,
        string AtUtc);

    /// <summary>
    /// TASK-16 U3 (ADR-65): wrist alert bridge. When a URL is set (env NEXUS_WATCH_WEBHOOK_URL, else nexus_config.json
    /// watchWebhookUrl; empty = off, nothing is created), a /timer expiry or a 3-strike escalation POSTs
    /// <c>{"event","title":"NEXUS Hub","message"}</c> as JSON, fire-and-forget with a 3 s timeout, so a push app on the
    /// iPhone 14 Pro Max raises a notification that the HUAWEI WATCH GT 6 mirrors. Test runs (selftest / mock Gemini) never
    /// send, except the explicit test call (/watch test, selftest ui-v2 to a loopback listener). A message that looks like
    /// a secret is replaced before sending. Cost: whatever the chosen push service charges; NEXUS adds none.
    /// </summary>
    public static class WatchWebhook
    {
        public const string UrlVariable = "NEXUS_WATCH_WEBHOOK_URL";
        public const int TimeoutMs = 3000;
        public const string TimerEvent = "timer_expired";
        public const string EscalationEvent = "escalated_3_strikes";
        public const string TestEvent = "test";
        public const string Title = "NEXUS Hub";

        private static readonly Lazy<HttpClient> Client = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        private static long _sent;

        /// <summary>Set by NexusRuntime so Core code without a config reference (escalation report) can notify.</summary>
        public static Func<NexusConfig>? ConfigProvider { get; set; }

        public static WatchWebhookResult? LastResult { get; private set; }

        public static long SentCount => Interlocked.Read(ref _sent);

        public static bool IsValidUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        public static string? ResolveUrl(NexusConfig? config)
        {
            string? env = Environment.GetEnvironmentVariable(UrlVariable)?.Trim();
            string? url = !string.IsNullOrEmpty(env) ? env : config?.WatchWebhookUrl;
            return IsValidUrl(url) ? url : null;
        }

        public static string? HostOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : null;

        /// <summary>Fire-and-forget entry point: never throws, never blocks the caller (the POST runs on the pool).</summary>
        public static Task<WatchWebhookResult> Notify(string eventName, string message, bool force = false, NexusConfig? config = null)
        {
            string at = DateTime.UtcNow.ToString("o");
            string? url = ResolveUrl(config ?? ConfigProvider?.Invoke());
            if (url == null) return Task.FromResult(new WatchWebhookResult(eventName, false, true, "no webhook URL", null, null, null, 0, at));
            if (TestRun.Active && !force)
                return Task.FromResult(new WatchWebhookResult(eventName, false, true, "test run", null, null, HostOf(url), 0, at));
            string safe = LocalNoteService.ContainsSecret(message) ? "[nội dung ẩn: giống khóa bí mật]" : message;
            if (safe.Length > 240) safe = safe.Substring(0, 239) + "…";
            return Task.Run(() => SendAsync(url, eventName, safe));
        }

        /// <summary>One POST (tests call it directly with a loopback URL). Records <see cref="LastResult"/>.</summary>
        public static async Task<WatchWebhookResult> SendAsync(string url, string eventName, string message)
        {
            long t0 = Stopwatch.GetTimestamp();
            string host = HostOf(url) ?? "?";
            WatchWebhookResult result;
            try
            {
                string json = JsonSerializer.Serialize(new { @event = eventName, title = Title, message });
                using var cts = new CancellationTokenSource(TimeoutMs);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await Client.Value.PostAsync(url, content, cts.Token).ConfigureAwait(false);
                result = new WatchWebhookResult(eventName, response.IsSuccessStatusCode, false, null, (int)response.StatusCode,
                    response.IsSuccessStatusCode ? null : response.ReasonPhrase, host, Ms(t0), DateTime.UtcNow.ToString("o"));
            }
            catch (OperationCanceledException)
            {
                result = new WatchWebhookResult(eventName, false, false, null, null, $"timeout {TimeoutMs} ms", host, Ms(t0), DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                result = new WatchWebhookResult(eventName, false, false, null, null, $"{ex.GetType().Name}: {ex.Message}", host, Ms(t0), DateTime.UtcNow.ToString("o"));
            }
            LastResult = result;
            Interlocked.Increment(ref _sent);
            return result;
        }

        private static double Ms(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 1);
    }
}
