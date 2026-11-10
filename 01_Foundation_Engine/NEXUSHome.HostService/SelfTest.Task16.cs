using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NEXUSHomeHub.Core;

namespace NEXUSHome.HostService
{
    internal static partial class SelfTest
    {
        // ───────────────────────────── TASK-16 (ADR-65) Cockpit UI v2 ─────────────────────────────

        /// <summary>Notify must hand the POST to the pool and return at once: far below the 3 s HTTP timeout.</summary>
        private const double WebhookReturnMaxMs = 50.0;

        /// <summary>
        /// TASK-16 "ui-v2" (headless, no window, 0 tokens, no real network: loopback only, no Program Files write):
        ///   U1  snap math 40 / 330 / 640, double-click toggle, /ui argument parser, cockpit_ui.json round trip (no BOM, broken
        ///       file = defaults), auto-collapse delay override
        ///   U2  Equalizer APO switcher in a temp folder (BOM + CRLF kept, one backup, unchanged = no write, missing profile /
        ///       not UTF-8 = untouched, duplicates removed, line appended when missing) + the G272 YouTube watcher with a simulated
        ///       browser title (one switch per state change, no event when nothing changes)
        ///   U3  webhook JSON {event,title,message} to a 127.0.0.1 listener, Notify returns in &lt; 50 ms while the server stalls,
        ///       the stalled POST ends at the 3 s timeout, test-run skip without force, secret-looking text redacted
        ///   B1  isTest snapshot flag + display filter rules; B5 card label
        /// </summary>
        private static (object Report, bool Pass) UiV2(NexusRuntime runtime)
        {
            var checks = new Dictionary<string, bool>();
            string temp = Path.Combine(Path.GetTempPath(), "nexus_task16_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            try
            {
                object u1 = UiV2Layout(temp, checks);
                object u2 = UiV2Equalizer(runtime, temp, checks);
                object u3 = UiV2Webhook(runtime, checks);
                object b1 = UiV2DisplayFilter(runtime, checks);
                bool pass = checks.Values.All(v => v);
                return (new
                {
                    scenario = "ui-v2",
                    pass,
                    failed = checks.Where(kv => !kv.Value).Select(kv => kv.Key).ToArray(),
                    checks,
                    u1,
                    u2,
                    u3,
                    b1
                }, pass);
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static object UiV2Layout(string temp, Dictionary<string, bool> checks)
        {
            var snaps = new Dictionary<string, string>
            {
                ["0"] = CockpitLayout.Snap(0), ["100"] = CockpitLayout.Snap(100), ["184"] = CockpitLayout.Snap(184),
                ["200"] = CockpitLayout.Snap(200), ["330"] = CockpitLayout.Snap(330), ["485"] = CockpitLayout.Snap(485),
                ["500"] = CockpitLayout.Snap(500), ["2000"] = CockpitLayout.Snap(2000)
            };
            checks["u1.snap"] = snaps["0"] == CockpitLayout.Collapsed && snaps["100"] == CockpitLayout.Collapsed &&
                                snaps["184"] == CockpitLayout.Collapsed && snaps["200"] == CockpitLayout.Half &&
                                snaps["330"] == CockpitLayout.Half && snaps["485"] == CockpitLayout.Half &&
                                snaps["500"] == CockpitLayout.Full && snaps["2000"] == CockpitLayout.Full;
            checks["u1.heights"] = CockpitLayout.HeightOf("collapsed") == 40 && CockpitLayout.HeightOf("half") == 330 &&
                                   CockpitLayout.HeightOf("full") == 640 && CockpitLayout.HeightOf("nonsense") == 330;
            checks["u1.toggle"] = CockpitLayout.ToggleCollapsed("collapsed") == CockpitLayout.Half &&
                                  CockpitLayout.ToggleCollapsed("half") == CockpitLayout.Collapsed &&
                                  CockpitLayout.ToggleCollapsed("full") == CockpitLayout.Collapsed;

            var parsed = new Dictionary<string, string?>
            {
                ["(empty)"] = CockpitLayout.ParseUiAction(""), ["half"] = CockpitLayout.ParseUiAction("half"),
                ["thu"] = CockpitLayout.ParseUiAction("thu"), ["640"] = CockpitLayout.ParseUiAction("640"),
                ["xuyen on"] = CockpitLayout.ParseUiAction("xuyen on"), ["clickthrough off"] = CockpitLayout.ParseUiAction("clickthrough off"),
                ["tuthu tắt"] = CockpitLayout.ParseUiAction("tuthu tắt"), ["keys"] = CockpitLayout.ParseUiAction("keys"),
                ["xuyen"] = CockpitLayout.ParseUiAction("xuyen"), ["fly"] = CockpitLayout.ParseUiAction("fly")
            };
            checks["u1.parser"] = parsed["(empty)"] == "status" && parsed["half"] == "level:half" && parsed["thu"] == "level:collapsed" &&
                                  parsed["640"] == "level:full" && parsed["xuyen on"] == "clickthrough:on" &&
                                  parsed["clickthrough off"] == "clickthrough:off" && parsed["tuthu tắt"] == "autocollapse:off" &&
                                  parsed["keys"] == "keys" && parsed["xuyen"] == null && parsed["fly"] == null;

            var store = new CockpitUiStore(temp);
            CockpitUiState defaults = store.Load();
            var saved = new CockpitUiState(CockpitLayout.Full, 2000, 64, AutoCollapse: false, ClickThrough: true);
            string? saveError = store.Save(saved);
            CockpitUiState loaded = store.Load();
            bool noBom = NoBom(store.Path);
            string json = File.ReadAllText(store.Path, Encoding.UTF8);
            File.WriteAllText(store.Path, "{ not json", new UTF8Encoding(false));
            CockpitUiState broken = store.Load();
            checks["u1.store"] = defaults == new CockpitUiState() && saveError == null && loaded == saved && noBom &&
                                 json.Contains("\"heightLevel\": \"full\"", StringComparison.Ordinal) &&
                                 broken == new CockpitUiState() && store.LastError != null;

            string? oldDelay = Environment.GetEnvironmentVariable(CockpitLayout.AutoCollapseDelayVariable);
            Environment.SetEnvironmentVariable(CockpitLayout.AutoCollapseDelayVariable, null);
            int defaultDelay = CockpitLayout.ResolveAutoCollapseDelayMs();
            Environment.SetEnvironmentVariable(CockpitLayout.AutoCollapseDelayVariable, "50");
            int clampedDelay = CockpitLayout.ResolveAutoCollapseDelayMs();
            Environment.SetEnvironmentVariable(CockpitLayout.AutoCollapseDelayVariable, oldDelay);
            checks["u1.autoCollapseDelay"] = defaultDelay == 30000 && clampedDelay == 1000;

            return new { snaps, parsed, defaults, saved, loaded, savedJson = json, brokenFileError = store.LastError, defaultDelay, clampedDelay };
        }

        private static object UiV2Equalizer(NexusRuntime runtime, string temp, Dictionary<string, bool> checks)
        {
            string dir = Path.Combine(temp, "eq");
            Directory.CreateDirectory(dir);
            string config = Path.Combine(dir, EqualizerApoSwitcher.ConfigFileName);
            File.WriteAllText(Path.Combine(dir, EqualizerApoSwitcher.ProfileFile("youtube")), "Preamp: -2 dB\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, EqualizerApoSwitcher.ProfileFile("music")), "Preamp: -4 dB\r\n", new UTF8Encoding(false));
            byte[] original = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes("Device: Samsung K450\r\nPreamp: -3 dB\r\nInclude: nexus_k450_music.txt\r\nGraphicEQ: 25 0; 40 1\r\n")).ToArray();
            File.WriteAllBytes(config, original);

            EqSwitchResult toYoutube = EqualizerApoSwitcher.Switch(dir, EqualizerApoSwitcher.YoutubeProfile);
            byte[] afterYoutube = File.ReadAllBytes(config);
            string afterYoutubeText = Encoding.UTF8.GetString(afterYoutube, 3, afterYoutube.Length - 3);
            bool backupOk = File.Exists(config + EqualizerApoSwitcher.BackupSuffix) &&
                            File.ReadAllBytes(config + EqualizerApoSwitcher.BackupSuffix).SequenceEqual(original);
            DateTime stamp = File.GetLastWriteTimeUtc(config);
            EqSwitchResult again = EqualizerApoSwitcher.Switch(dir, EqualizerApoSwitcher.YoutubeProfile);
            bool unchangedNoWrite = !again.Changed && again.Ok && File.GetLastWriteTimeUtc(config) == stamp &&
                                    File.ReadAllBytes(config).SequenceEqual(afterYoutube);
            EqSwitchResult toMusic = EqualizerApoSwitcher.Switch(dir, EqualizerApoSwitcher.MusicProfile);
            bool roundTrip = File.ReadAllBytes(config).SequenceEqual(original);
            checks["u2.switchYoutube"] = toYoutube.Ok && toYoutube.Changed && toYoutube.BackupWritten && backupOk &&
                                         afterYoutube.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()) &&
                                         afterYoutubeText == "Device: Samsung K450\r\nPreamp: -3 dB\r\nInclude: nexus_k450_youtube.txt\r\nGraphicEQ: 25 0; 40 1\r\n";
            checks["u2.unchangedNoWrite"] = unchangedNoWrite;
            checks["u2.switchMusicRoundTrip"] = toMusic.Ok && toMusic.Changed && !toMusic.BackupWritten && roundTrip &&
                                                EqualizerApoSwitcher.CurrentProfile(dir) == "music";

            // missing profile file / not UTF-8 / no include line / duplicates
            string dir2 = Path.Combine(temp, "eq2");
            Directory.CreateDirectory(dir2);
            string config2 = Path.Combine(dir2, EqualizerApoSwitcher.ConfigFileName);
            File.WriteAllText(config2, "Preamp: 0 dB\n", new UTF8Encoding(false));
            EqSwitchResult missing = EqualizerApoSwitcher.Switch(dir2, EqualizerApoSwitcher.YoutubeProfile);
            bool missingUntouched = !missing.Ok && File.ReadAllText(config2) == "Preamp: 0 dB\n" && !File.Exists(config2 + EqualizerApoSwitcher.BackupSuffix);
            File.WriteAllText(Path.Combine(dir2, EqualizerApoSwitcher.ProfileFile("youtube")), "x", new UTF8Encoding(false));
            EqSwitchResult appended = EqualizerApoSwitcher.Switch(dir2, EqualizerApoSwitcher.YoutubeProfile);
            string appendedText = File.ReadAllText(config2);
            File.WriteAllText(config2, "Include: nexus_k450_music.txt\nPreamp: 0 dB\ninclude:  NEXUS_K450_music.txt\n# Include: nexus_k450_music.txt\n", new UTF8Encoding(false));
            EqSwitchResult dedup = EqualizerApoSwitcher.Switch(dir2, EqualizerApoSwitcher.YoutubeProfile);
            string dedupText = File.ReadAllText(config2);
            byte[] latin1 = { 0x50, 0x72, 0x65, 0x61, 0x6D, 0x70, 0x3A, 0x20, 0xE9, 0x0A };
            File.WriteAllBytes(config2, latin1);
            EqSwitchResult notUtf8 = EqualizerApoSwitcher.Switch(dir2, EqualizerApoSwitcher.MusicProfile);
            File.WriteAllText(Path.Combine(dir2, EqualizerApoSwitcher.ProfileFile("music")), "x", new UTF8Encoding(false));
            notUtf8 = EqualizerApoSwitcher.Switch(dir2, EqualizerApoSwitcher.MusicProfile);
            bool notUtf8Untouched = !notUtf8.Ok && File.ReadAllBytes(config2).SequenceEqual(latin1);
            checks["u2.missingProfileUntouched"] = missingUntouched;
            checks["u2.appendWhenNoInclude"] = appended.Ok && appended.Changed && appendedText == "Preamp: 0 dB\nInclude: nexus_k450_youtube.txt\n";
            checks["u2.dedup"] = dedup.Ok && dedupText == "Include: nexus_k450_youtube.txt\nPreamp: 0 dB\n# Include: nexus_k450_music.txt\n";
            checks["u2.notUtf8Untouched"] = notUtf8Untouched;
            checks["u2.includeParser"] = EqualizerApoSwitcher.NexusIncludeFile("  Include: nexus_k450_youtube.txt ") == "nexus_k450_youtube.txt" &&
                                         EqualizerApoSwitcher.NexusIncludeFile("Include: other.txt") == null &&
                                         EqualizerApoSwitcher.NexusIncludeFile("# Include: nexus_k450_music.txt") == null &&
                                         EqualizerApoSwitcher.NexusIncludeFile("Include: ..\\nexus_k450_music.txt") == null;

            // the G272 watcher with a simulated browser title (no window enumeration, no Program Files)
            checks["u2.titleRules"] = YouTubeG272Watcher.IsYouTubeTitle("Lofi radio - YouTube — Google Chrome") &&
                                      !YouTubeG272Watcher.IsYouTubeTitle("Visual Studio Code") &&
                                      YouTubeG272Watcher.IsBrowser("msedge") && YouTubeG272Watcher.IsBrowser("Firefox") &&
                                      !YouTubeG272Watcher.IsBrowser("Code");
            string? oldDir = Environment.GetEnvironmentVariable(EqualizerApoSwitcher.DirVariable);
            string? oldTitle = YouTubeG272Watcher.SimulatedTitle;
            var watcher = new YouTubeG272Watcher(runtime.Config);
            var events = new List<object>();
            watcher.Changed += (watching, check, eq) => events.Add(new { watching, profile = eq?.Profile, ok = eq?.Ok, changed = eq?.Changed });
            object watcherReport;
            try
            {
                Environment.SetEnvironmentVariable(EqualizerApoSwitcher.DirVariable, dir);
                YouTubeG272Watcher.SimulatedTitle = "Lofi radio - YouTube — Google Chrome";
                bool active = watcher.Configure();
                YouTubeCheck c1 = watcher.CheckNow("selftest");
                string? profile1 = EqualizerApoSwitcher.CurrentProfile(dir);
                YouTubeCheck c2 = watcher.CheckNow("selftest");
                int eventsAfterRepeat = events.Count;
                YouTubeG272Watcher.SimulatedTitle = "Spotify Premium";
                YouTubeCheck c3 = watcher.CheckNow("selftest");
                string? profile3 = EqualizerApoSwitcher.CurrentProfile(dir);
                bool autoSwitch = runtime.Config.Current.YoutubeEqAutoSwitch;
                checks["u2.watcherSimulated"] = !autoSwitch || (active && c1.Watching && c1.Simulated && profile1 == "youtube" &&
                                                                 c2.Watching && eventsAfterRepeat == 1 && !c3.Watching &&
                                                                 profile3 == "music" && events.Count == 2);
                watcherReport = new { autoSwitch, active, c1, c2, c3, profile1, profile3, events, passes = watcher.Passes };
            }
            finally
            {
                Environment.SetEnvironmentVariable(EqualizerApoSwitcher.DirVariable, oldDir);
                YouTubeG272Watcher.SimulatedTitle = oldTitle;
            }

            return new
            {
                toYoutube, again, toMusic, missing, appended, dedup, notUtf8, appendedText, dedupText, afterYoutubeText,
                watcher = watcherReport
            };
        }

        private static object UiV2Webhook(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            // 1) a listener that answers 204: the JSON body must be {event, title:"NEXUS Hub", message}
            var answering = new TcpListener(IPAddress.Loopback, 0);
            answering.Start();
            int port = ((IPEndPoint)answering.LocalEndpoint).Port;
            Task<string> serverTask = Task.Run(() => ServeOnce(answering, respond: true));
            string url = $"http://127.0.0.1:{port}/nexus-hook";
            WatchWebhookResult direct = WatchWebhook.SendAsync(url, WatchWebhook.TestEvent, "Thử rung từ selftest ui-v2").GetAwaiter().GetResult();
            string raw = serverTask.Wait(5000) ? serverTask.Result : string.Empty;
            answering.Stop();
            string body = raw.Contains("\r\n\r\n", StringComparison.Ordinal) ? raw.Substring(raw.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4) : string.Empty;
            string? evt = null, title = null, message = null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                evt = doc.RootElement.GetProperty("event").GetString();
                title = doc.RootElement.GetProperty("title").GetString();
                message = doc.RootElement.GetProperty("message").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            checks["u3.postJson"] = direct.Ok && direct.StatusCode == 204 && raw.StartsWith("POST /nexus-hook ", StringComparison.Ordinal) &&
                                    raw.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
                                    evt == WatchWebhook.TestEvent && title == "NEXUS Hub" && message == "Thử rung từ selftest ui-v2";

            // 2) a listener that accepts and never answers: Notify returns at once, the POST ends at the 3 s timeout
            var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            int silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
            Task<string> silentTask = Task.Run(() => ServeOnce(silent, respond: false));
            NexusConfig withUrl = runtime.Config.Current with { WatchWebhookUrl = $"http://127.0.0.1:{silentPort}/stall" };
            string? envUrl = Environment.GetEnvironmentVariable(WatchWebhook.UrlVariable);
            Environment.SetEnvironmentVariable(WatchWebhook.UrlVariable, null);
            WatchWebhookResult stalled, skippedTestRun, skippedNoUrl, redacted;
            double returnMs;
            string redactedBody;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                Task<WatchWebhookResult> pending = WatchWebhook.Notify(WatchWebhook.TimerEvent, "Hết giờ: ui-v2", force: true, config: withUrl);
                returnMs = Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
                bool notDoneYet = !pending.IsCompleted;
                stalled = pending.GetAwaiter().GetResult();
                checks["u3.nonBlocking"] = returnMs < WebhookReturnMaxMs && notDoneYet;
                checks["u3.timeout3s"] = !stalled.Ok && stalled.Error != null && stalled.Error.StartsWith("timeout", StringComparison.Ordinal) &&
                                         stalled.ElapsedMs >= 2800 && stalled.ElapsedMs <= 4500;

                skippedTestRun = WatchWebhook.Notify(WatchWebhook.EscalationEvent, "x", force: false, config: withUrl).GetAwaiter().GetResult();
                skippedNoUrl = WatchWebhook.Notify(WatchWebhook.EscalationEvent, "x", force: true, config: runtime.Config.Current with { WatchWebhookUrl = "" })
                    .GetAwaiter().GetResult();
                checks["u3.skipRules"] = skippedTestRun.Skipped && skippedTestRun.SkipReason == "test run" &&
                                         skippedNoUrl.Skipped && skippedNoUrl.SkipReason == "no webhook URL";

                var answering2 = new TcpListener(IPAddress.Loopback, 0);
                answering2.Start();
                int port2 = ((IPEndPoint)answering2.LocalEndpoint).Port;
                Task<string> redactTask = Task.Run(() => ServeOnce(answering2, respond: true));
                string fakeSecret = "sk-" + new string('a', 24); // built at run time, never a literal
                redacted = WatchWebhook.Notify(WatchWebhook.EscalationEvent, $"lỗi với khóa {fakeSecret}", force: true,
                    config: runtime.Config.Current with { WatchWebhookUrl = $"http://127.0.0.1:{port2}/r" }).GetAwaiter().GetResult();
                redactedBody = redactTask.Wait(5000) ? redactTask.Result : string.Empty;
                answering2.Stop();
                string redactedMessage = string.Empty;
                int bodyAt = redactedBody.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                try
                {
                    if (bodyAt >= 0)
                    {
                        using JsonDocument doc = JsonDocument.Parse(redactedBody.Substring(bodyAt + 4));
                        redactedMessage = doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                checks["u3.secretRedacted"] = redacted.Ok && !redactedBody.Contains(fakeSecret, StringComparison.Ordinal) &&
                                              redactedMessage.Contains("nội dung ẩn", StringComparison.Ordinal) && !redactedMessage.Contains(fakeSecret, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(WatchWebhook.UrlVariable, envUrl);
                silent.Stop();
            }
            checks["u3.validUrl"] = WatchWebhook.IsValidUrl("https://ntfy.sh/abc") && !WatchWebhook.IsValidUrl("ftp://x/y") &&
                                    !WatchWebhook.IsValidUrl("not a url") && WatchWebhook.HostOf("https://ntfy.sh/topic?token=1") == "ntfy.sh";
            return new
            {
                direct, requestLine = raw.Split("\r\n")[0], body, returnMs, returnMaxMs = WebhookReturnMaxMs, stalled,
                skippedTestRun, skippedNoUrl, redacted, sentCount = WatchWebhook.SentCount
            };
        }

        /// <summary>Accepts one HTTP request on the loopback listener; answers 204 or never answers (stall until the client gives up).</summary>
        private static string ServeOnce(TcpListener listener, bool respond)
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                client.ReceiveTimeout = 5000;
                using NetworkStream stream = client.GetStream();
                var buffer = new byte[8192];
                var received = new MemoryStream();
                int headerEnd = -1, contentLength = 0;
                while (true)
                {
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    received.Write(buffer, 0, n);
                    string sofar = Encoding.UTF8.GetString(received.ToArray());
                    if (headerEnd < 0 && (headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        foreach (string line in sofar.Substring(0, headerEnd).Split("\r\n"))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    if (headerEnd >= 0 && received.Length >= headerEnd + 4 + contentLength) break;
                }
                string request = Encoding.UTF8.GetString(received.ToArray());
                if (respond)
                {
                    byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    stream.Write(reply, 0, reply.Length);
                }
                else
                {
                    Thread.Sleep(4000); // longer than the 3 s client timeout
                }
                return request;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return string.Empty;
            }
        }

        private static object UiV2DisplayFilter(NexusRuntime runtime, Dictionary<string, bool> checks)
        {
            var processor = new CockpitCommandProcessor(runtime);
            CockpitCommandResult calc = processor.Execute("/calc 2+3", IntPtr.Zero);
            LatestDispatchSnapshot? stamped = calc.Snapshot == null ? null : LatestDispatchFile.WithWidgetCards(calc.Snapshot);
            LatestDispatchSnapshot? onDisk = LatestDispatchFile.TryRead(runtime.LatestDispatchPath);
            string diskJson = File.Exists(runtime.LatestDispatchPath) ? File.ReadAllText(runtime.LatestDispatchPath, Encoding.UTF8) : string.Empty;
            bool realLooking = stamped != null && !CockpitDisplayFilter.IsTestSnapshot(stamped with { IsTest = null });
            checks["b1.isTestStamped"] = TestRun.Active && stamped?.IsTest == true && onDisk?.IsTest == true &&
                                         (diskJson.Contains("\"isTest\": true", StringComparison.Ordinal) || diskJson.Contains("\"isTest\":true", StringComparison.Ordinal));
            checks["b1.filterRules"] = stamped != null && CockpitDisplayFilter.IsTestSnapshot(stamped) && realLooking &&
                                       CockpitDisplayFilter.LooksLikeTest("gemini-mock", "x") &&
                                       CockpitDisplayFilter.LooksLikeTest("gemini-2.5-flash", "Thiết kế API (Gate 12 E2)") &&
                                       !CockpitDisplayFilter.LooksLikeTest("gemini-2.5-flash", "Thiết kế API cho NEXUS") &&
                                       CockpitDisplayFilter.IsTestDispatch(new RecentDispatch(1, "", "Micro", "SQ", "a", "b", "t", "PASSED", AiModel: "mock")) &&
                                       !CockpitDisplayFilter.IsTestDispatch(new RecentDispatch(2, "", "Micro", "SQ", "a", "b", "Viết hàm C#", "PASSED")) &&
                                       !CockpitDisplayFilter.HideTestRecords; // a selftest is a test run: it still sees everything
            IReadOnlyList<HudWidgetCard> cards = WidgetCardExtractor.Extract("```csharp\nvar x = 1;\n```\n- [ ] a\n- [ ] b");
            string label = WidgetCardExtractor.CardCountLabel(cards);
            string empty = WidgetCardExtractor.CardCountLabel(Array.Empty<HudWidgetCard>());
            checks["b5.cardLabel"] = label == $"Câu trả lời hiện tại: {cards.Count} thẻ" && cards.Count >= 1 && empty == "Câu trả lời hiện tại: 0 thẻ";
            return new { calcOk = calc.Ok, stampedIsTest = stamped?.IsTest, onDiskIsTest = onDisk?.IsTest, realLooking, label, empty, indicator = WidgetCardExtractor.Indicator(cards) };
        }
    }
}
