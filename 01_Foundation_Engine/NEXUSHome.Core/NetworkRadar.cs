using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NEXUSHomeHub.Core
{
    /// <summary>TASK-17 N1: one network adapter as /net sees it (IPv4 only; the LAN of this project is IPv4).</summary>
    public sealed record NetInterfaceInfo(
        string Name,
        string Description,
        string Type,
        string? IPv4,
        string? Gateway,
        bool Virtual,
        string? VirtualReason);

    /// <summary>One ICMP echo with a hard limit. <see cref="Status"/> = Success | TimedOut | an IPStatus name | ERROR.</summary>
    public sealed record NetPingSample(string Label, string Host, bool Ok, long? RttMs, string Status, double ElapsedMs);

    /// <summary>/net ping: N echoes (sequential), min / avg / max of the answered ones.</summary>
    public sealed record NetPingSeries(string Host, string? Address, IReadOnlyList<NetPingSample> Samples, int Answered,
        long? MinMs, double? AvgMs, long? MaxMs, double ElapsedMs, string? Error);

    /// <summary>A TCP listener / connection or a UDP endpoint of this PC.</summary>
    public sealed record NetPortEntry(string Protocol, int Port, string State, string Local, string? Remote);

    /// <summary>/net port: what holds one local port right now (FREE = nothing listens, no connection, no UDP socket).</summary>
    public sealed record NetPortCheck(int Port, string State, bool TcpListening, int TcpConnections, bool UdpBound,
        IReadOnlyList<NetPortEntry> Matches, double ElapsedMs);

    /// <summary>/net status (the Radar card, MCP get_network_radar, the right-column NITRO5 line and the Alt+Q page).</summary>
    public sealed record NetRadarSnapshot(
        string AtUtc,
        NetInterfaceInfo? ActiveNic,
        IReadOnlyList<NetInterfaceInfo> SkippedVirtual,
        IReadOnlyList<NetPingSample> Pings,
        string Nitro5State,
        string? Nitro5Address,
        long? Nitro5RttMs,
        IReadOnlyList<NetPortEntry> TopListening,
        int TcpListening,
        int TcpEstablished,
        int UdpBound,
        double NicMs,
        double PingMs,
        double PortsMs,
        double TotalMs,
        string? PortFilter);

    /// <summary>
    /// TASK-17 N1 (ADR-67) network and socket radar, on demand only: nothing here runs unless /net, the Alt+Q page or the MCP
    /// tool asks (0 % idle CPU, no timer, no background loop). Pings run in parallel and every one is cut at
    /// <see cref="PingTimeoutMs"/> by the caller even when the OS echo takes longer (ARP for a sleeping host can take seconds):
    /// a laptop that is away is reported "OFFLINE (AWAY)" in about 200 ms and never blocks the UI thread
    /// (docs/NITRO5_WORKER_NODE.md §4). The adapter shown is the physical one with the LAN gateway 192.168.1.1; virtual
    /// adapters (Radmin VPN 26.x, Hyper-V, VPN, Bluetooth …) are listed as skipped.
    /// </summary>
    public sealed class NetworkRadar
    {
        public const int PingTimeoutMs = 200;
        public const int PingSeriesSamples = 3;
        public const string LanGateway = "192.168.1.1";
        public const string Nitro5Ethernet = "192.168.1.22";
        public const string Nitro5WiFi = "192.168.1.192";
        public const string LanTv = "192.168.1.97";
        public const string Nitro5Offline = "OFFLINE (AWAY)";
        public const int TopPortCount = 8;

        /// <summary>Ports a developer of this project looks for first (NEXUS LAN services, Redis, common dev servers, databases).</summary>
        private static readonly int[] PriorityPorts = { 5055, 5056, 6379, 5000, 5001, 8080, 8000, 3000, 5173, 1433, 3306, 5432, 27017, 8888 };

        private static readonly string[] VirtualMarkers =
        {
            "Radmin", "VPN", "Virtual", "Hyper-V", "vEthernet", "VMware", "VirtualBox", "TAP-", "WireGuard", "Tailscale",
            "ZeroTier", "Hamachi", "Npcap", "Loopback", "Bluetooth", "Teredo", "isatap"
        };

        private NetRadarSnapshot? _last;

        /// <summary>The newest /net status scan of this process (null before the first one).</summary>
        public NetRadarSnapshot? Last => Volatile.Read(ref _last);

        /// <summary>Raised on the scanning thread after every status scan.</summary>
        public event Action<NetRadarSnapshot>? Scanned;

        /// <summary>A target of the status scan (selftests replace the LAN addresses with loopback / TEST-NET ones).</summary>
        public sealed record Target(string Label, string Host);

        public static IReadOnlyList<Target> DefaultTargets(string? gateway) => new[]
        {
            new Target("GW", string.IsNullOrWhiteSpace(gateway) ? LanGateway : gateway!),
            new Target("NITRO5", Nitro5Ethernet),
            new Target("NITRO5-WIFI", Nitro5WiFi),
            new Target("TV", LanTv)
        };

        /// <summary>Adapters that are up (loopback and tunnels left out), each with its first IPv4 address and IPv4 gateway.</summary>
        public static IReadOnlyList<NetInterfaceInfo> ReadInterfaces()
        {
            var list = new List<NetInterfaceInfo>();
            NetworkInterface[] all;
            try
            {
                all = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return list;
            }
            foreach (NetworkInterface nic in all)
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string? ipv4 = null;
                string? gateway = null;
                try
                {
                    IPInterfaceProperties props = nic.GetIPProperties();
                    ipv4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                    gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                         !g.Address.Equals(IPAddress.Any))?.Address.ToString();
                }
                catch (NetworkInformationException)
                {
                    // adapter vanished while being read
                }
                string? reason = VirtualReason(nic.Name, nic.Description, ipv4);
                list.Add(new NetInterfaceInfo(nic.Name, nic.Description, nic.NetworkInterfaceType.ToString(), ipv4, gateway, reason != null, reason));
            }
            return list;
        }

        /// <summary>Why an adapter is not the LAN one (null = physical). Radmin VPN hands out 26.x addresses.</summary>
        public static string? VirtualReason(string name, string description, string? ipv4)
        {
            if (ipv4 != null && ipv4.StartsWith("26.", StringComparison.Ordinal)) return "26.x (Radmin VPN)";
            foreach (string marker in VirtualMarkers)
            {
                if (name.Contains(marker, StringComparison.OrdinalIgnoreCase) || description.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return marker;
            }
            return null;
        }

        /// <summary>Physical adapter with the LAN gateway 192.168.1.1, else a physical one with any gateway, else any with IPv4.</summary>
        public static NetInterfaceInfo? PickActive(IReadOnlyList<NetInterfaceInfo> interfaces) =>
            interfaces.FirstOrDefault(i => !i.Virtual && i.Gateway == LanGateway && i.IPv4 != null)
            ?? interfaces.FirstOrDefault(i => !i.Virtual && i.Gateway != null && i.IPv4 != null)
            ?? interfaces.FirstOrDefault(i => !i.Virtual && i.IPv4 != null);

        /// <summary>
        /// /net status. Adapter list, the parallel pings (each cut at <paramref name="timeoutMs"/>) and the port tables. Runs on
        /// the calling thread up to the first await; callers on the UI thread use Task.Run.
        /// </summary>
        public async Task<NetRadarSnapshot> ScanAsync(IReadOnlyList<Target>? targets = null, int timeoutMs = PingTimeoutMs, int? portFilter = null)
        {
            long t0 = Stopwatch.GetTimestamp();
            IReadOnlyList<NetInterfaceInfo> nics = ReadInterfaces();
            NetInterfaceInfo? active = PickActive(nics);
            double nicMs = Ms(t0);

            long t1 = Stopwatch.GetTimestamp();
            targets ??= DefaultTargets(active?.Gateway);
            NetPingSample[] pings = await Task.WhenAll(targets.Select(t => PingOnceAsync(t.Label, t.Host, timeoutMs))).ConfigureAwait(false);
            double pingMs = Ms(t1);

            long t2 = Stopwatch.GetTimestamp();
            var (top, listening, established, udp) = ReadPorts(TopPortCount, portFilter);
            double portsMs = Ms(t2);

            NetPingSample? nitroEth = pings.FirstOrDefault(p => p.Label == "NITRO5");
            NetPingSample? nitroWifi = pings.FirstOrDefault(p => p.Label == "NITRO5-WIFI");
            string nitroState;
            string? nitroAddress = null;
            long? nitroRtt = null;
            if (nitroEth is { Ok: true })
            {
                nitroState = "ONLINE (Ethernet)";
                nitroAddress = nitroEth.Host;
                nitroRtt = nitroEth.RttMs;
            }
            else if (nitroWifi is { Ok: true })
            {
                nitroState = "ONLINE (Wi-Fi)";
                nitroAddress = nitroWifi.Host;
                nitroRtt = nitroWifi.RttMs;
            }
            else
            {
                nitroState = Nitro5Offline;
            }

            var snapshot = new NetRadarSnapshot(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), active,
                nics.Where(n => n.Virtual).ToList(), pings, nitroState, nitroAddress, nitroRtt, top, listening, established, udp,
                nicMs, pingMs, portsMs, Ms(t0), portFilter?.ToString(CultureInfo.InvariantCulture));
            Volatile.Write(ref _last, snapshot);
            try
            {
                Scanned?.Invoke(snapshot);
            }
            catch (Exception)
            {
                // a listener's problem is not the scan's
            }
            return snapshot;
        }

        /// <summary>
        /// One ICMP echo, never longer than <paramref name="timeoutMs"/> for the caller: the OS echo keeps running in the background
        /// when it overruns (and is disposed when it ends), the sample says TimedOut.
        /// </summary>
        public static async Task<NetPingSample> PingOnceAsync(string label, string host, int timeoutMs = PingTimeoutMs)
        {
            long t0 = Stopwatch.GetTimestamp();
            timeoutMs = Math.Clamp(timeoutMs, 20, 5000);
            Ping ping = new();
            Task<PingReply> echo;
            try
            {
                echo = ping.SendPingAsync(host, timeoutMs);
            }
            catch (Exception ex) when (ex is PingException or ArgumentException or InvalidOperationException or SocketException)
            {
                ping.Dispose();
                return new NetPingSample(label, host, false, null, "ERROR: " + (ex.InnerException?.Message ?? ex.Message), Ms(t0));
            }
            Task first = await Task.WhenAny(echo, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (first != echo)
            {
                _ = echo.ContinueWith(_ => ping.Dispose(), TaskScheduler.Default);
                return new NetPingSample(label, host, false, null, "TimedOut", Ms(t0));
            }
            try
            {
                PingReply reply = await echo.ConfigureAwait(false);
                bool ok = reply.Status == IPStatus.Success;
                return new NetPingSample(label, host, ok, ok ? reply.RoundtripTime : null, reply.Status.ToString(), Ms(t0));
            }
            catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
            {
                return new NetPingSample(label, host, false, null, "ERROR: " + (ex.InnerException?.Message ?? ex.Message), Ms(t0));
            }
            finally
            {
                ping.Dispose();
            }
        }

        /// <summary>/net ping &lt;host&gt;: <paramref name="samples"/> echoes one after the other (name lookup cut at 1 s).</summary>
        public static async Task<NetPingSeries> PingSeriesAsync(string host, int samples = PingSeriesSamples, int timeoutMs = PingTimeoutMs)
        {
            long t0 = Stopwatch.GetTimestamp();
            host = (host ?? string.Empty).Trim();
            if (host.Length == 0) return new NetPingSeries(host, null, Array.Empty<NetPingSample>(), 0, null, null, null, 0, "thiếu địa chỉ");
            string? address = null;
            if (IPAddress.TryParse(host, out IPAddress? ip))
            {
                address = ip.ToString();
            }
            else
            {
                try
                {
                    Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
                    if (await Task.WhenAny(lookup, Task.Delay(1000)).ConfigureAwait(false) != lookup)
                        return new NetPingSeries(host, null, Array.Empty<NetPingSample>(), 0, null, null, null, Ms(t0), "DNS quá 1 s");
                    IPAddress[] found = await lookup.ConfigureAwait(false);
                    address = (found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault())?.ToString();
                }
                catch (SocketException ex)
                {
                    return new NetPingSeries(host, null, Array.Empty<NetPingSample>(), 0, null, null, null, Ms(t0), "DNS: " + ex.Message);
                }
                if (address == null) return new NetPingSeries(host, null, Array.Empty<NetPingSample>(), 0, null, null, null, Ms(t0), "DNS: không có địa chỉ");
            }
            var list = new List<NetPingSample>(samples);
            for (int i = 0; i < Math.Clamp(samples, 1, 10); i++) list.Add(await PingOnceAsync($"#{i + 1}", address, timeoutMs).ConfigureAwait(false));
            long[] rtts = list.Where(s => s.Ok && s.RttMs.HasValue).Select(s => s.RttMs!.Value).ToArray();
            return new NetPingSeries(host, address, list, rtts.Length,
                rtts.Length > 0 ? rtts.Min() : null, rtts.Length > 0 ? Math.Round(rtts.Average(), 1) : null, rtts.Length > 0 ? rtts.Max() : null,
                Ms(t0), null);
        }

        /// <summary>
        /// Gate 17 run 1 (Nhat Hoa's PC): GetActiveTcpListeners() missed a loopback TcpListener that GetActiveTcpConnections()
        /// returned as State Unknown with remote 0.0.0.0:0. A row whose remote end is port 0 (or State Listen) is a listener, so
        /// both tables are merged; established = State Established with a real remote end.
        /// </summary>
        private static bool IsListenerRow(TcpConnectionInformation c) =>
            c.State == TcpState.Listen || c.RemoteEndPoint.Port == 0;

        private static List<IPEndPoint> AllListeners(IPEndPoint[] listeners, TcpConnectionInformation[] connections)
        {
            var all = new List<IPEndPoint>(listeners);
            foreach (TcpConnectionInformation c in connections)
            {
                if (IsListenerRow(c) && !all.Any(l => l.Port == c.LocalEndPoint.Port && l.Address.Equals(c.LocalEndPoint.Address))) all.Add(c.LocalEndPoint);
            }
            return all;
        }

        /// <summary>Port tables of this PC: the top listening TCP ports (dev / NEXUS ports first), counts, optional one-port filter.</summary>
        public static (IReadOnlyList<NetPortEntry> Top, int TcpListening, int TcpEstablished, int UdpBound) ReadPorts(int top = TopPortCount, int? portFilter = null)
        {
            IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] listeners;
            TcpConnectionInformation[] connections;
            IPEndPoint[] udp;
            try
            {
                listeners = props.GetActiveTcpListeners();
                connections = props.GetActiveTcpConnections();
                udp = props.GetActiveUdpListeners();
            }
            catch (NetworkInformationException)
            {
                return (Array.Empty<NetPortEntry>(), 0, 0, 0);
            }
            int established = connections.Count(c => c.State == TcpState.Established && !IsListenerRow(c));
            List<IPEndPoint> allListeners = AllListeners(listeners, connections);
            IEnumerable<IPEndPoint> pick = portFilter.HasValue ? allListeners.Where(l => l.Port == portFilter.Value) : allListeners;
            List<NetPortEntry> list = pick
                .GroupBy(l => l.Port)
                .Select(g => g.OrderBy(l => l.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First())
                .OrderBy(l =>
                {
                    int i = Array.IndexOf(PriorityPorts, l.Port);
                    return i >= 0 ? i : PriorityPorts.Length;
                })
                .ThenBy(l => l.Port)
                .Take(Math.Max(1, top))
                .Select(l => new NetPortEntry("TCP", l.Port, "LISTENING", l.ToString(), null))
                .ToList();
            return (list, allListeners.Select(l => l.Port).Distinct().Count(), established, udp.Length);
        }

        /// <summary>/net port &lt;p&gt;: FREE, LISTENING (TCP), ESTABLISHED (a TCP connection on that local port) or UDP.</summary>
        public static NetPortCheck CheckPort(int port)
        {
            long t0 = Stopwatch.GetTimestamp();
            IPGlobalProperties props = IPGlobalProperties.GetIPGlobalProperties();
            var matches = new List<NetPortEntry>();
            bool listening = false;
            int tcpConnections = 0;
            bool udpBound = false;
            try
            {
                foreach (IPEndPoint l in props.GetActiveTcpListeners())
                {
                    if (l.Port != port) continue;
                    listening = true;
                    matches.Add(new NetPortEntry("TCP", port, "LISTENING", l.ToString(), null));
                }
                foreach (TcpConnectionInformation c in props.GetActiveTcpConnections())
                {
                    if (c.LocalEndPoint.Port != port) continue;
                    if (IsListenerRow(c))
                    {
                        if (!listening || !matches.Any(m => m.State == "LISTENING" && m.Local == c.LocalEndPoint.ToString()))
                            matches.Add(new NetPortEntry("TCP", port, "LISTENING", c.LocalEndPoint.ToString(), null));
                        listening = true;
                        continue;
                    }
                    tcpConnections++;
                    if (matches.Count < 12) matches.Add(new NetPortEntry("TCP", port, c.State.ToString().ToUpperInvariant(), c.LocalEndPoint.ToString(), c.RemoteEndPoint.ToString()));
                }
                foreach (IPEndPoint u in props.GetActiveUdpListeners())
                {
                    if (u.Port != port) continue;
                    udpBound = true;
                    matches.Add(new NetPortEntry("UDP", port, "BOUND", u.ToString(), null));
                }
            }
            catch (NetworkInformationException)
            {
                // reported as FREE with no match; the message says the tables could not be read
            }
            string state = listening ? "LISTENING" : tcpConnections > 0 ? "ESTABLISHED" : udpBound ? "UDP" : "FREE";
            return new NetPortCheck(port, state, listening, tcpConnections, udpBound, matches, Ms(t0));
        }

        public static string DescribePort(NetPortCheck c)
        {
            string detail = c.State switch
            {
                "FREE" => "trống (không TCP listener, không kết nối, không UDP)",
                "LISTENING" => $"đang LISTENING ({string.Join(", ", c.Matches.Where(m => m.State == "LISTENING").Select(m => m.Local))})" +
                               (c.TcpConnections > 0 ? $" + {c.TcpConnections} kết nối" : string.Empty) + (c.UdpBound ? " + UDP" : string.Empty),
                "ESTABLISHED" => $"không listener, {c.TcpConnections} kết nối TCP dùng cổng này" + (c.UdpBound ? " + UDP" : string.Empty),
                _ => "chỉ có socket UDP"
            };
            return $"[NET] cổng {c.Port}: {c.State} · {detail} · {Format(c.ElapsedMs)} ms";
        }

        public static string DescribePing(NetPingSeries s)
        {
            if (s.Error != null) return $"[NET] ping {s.Host}: lỗi {s.Error}";
            string target = s.Address != null && s.Address != s.Host ? $"{s.Host} ({s.Address})" : s.Host;
            if (s.Answered == 0)
                return $"[NET] ping {target}: 0/{s.Samples.Count} trả lời (giới hạn {PingTimeoutMs} ms/mẫu: {string.Join(", ", s.Samples.Select(x => x.Status).Distinct())})";
            return $"[NET] ping {target}: {s.Answered}/{s.Samples.Count} · min {s.MinMs} / avg {Format(s.AvgMs ?? 0)} / max {s.MaxMs} ms";
        }

        /// <summary>Thread one-liner and pipe / log message of a status scan.</summary>
        public static string Summary(NetRadarSnapshot s)
        {
            string nic = s.ActiveNic != null ? $"{s.ActiveNic.Name} {s.ActiveNic.IPv4}" : "không có NIC vật lý";
            return $"[NET] {nic} · " + string.Join(" · ", s.Pings.Where(p => p.Label != "NITRO5" && p.Label != "NITRO5-WIFI").Select(PingText)) +
                   $" · NITRO5 {Nitro5Short(s)} · LISTEN {s.TcpListening} · ESTAB {s.TcpEstablished} · {Format(s.TotalMs)} ms";
        }

        /// <summary>The Radar card body (3–5 short lines, monospace).</summary>
        public static string Describe(NetRadarSnapshot s)
        {
            var sb = new StringBuilder();
            if (s.ActiveNic != null)
                sb.Append("NIC   ").Append(s.ActiveNic.Name).Append(" · ").Append(s.ActiveNic.IPv4).Append(" · GW ").Append(s.ActiveNic.Gateway ?? "—");
            else
                sb.Append("NIC   không có card vật lý có IPv4");
            if (s.SkippedVirtual.Count > 0)
                sb.Append("  (bỏ qua: ").Append(string.Join(", ", s.SkippedVirtual.Take(3).Select(v => v.IPv4 != null ? $"{v.Name} {v.IPv4}" : v.Name))).Append(')');
            sb.Append('\n');
            sb.Append("PING  ").Append(string.Join(" · ", s.Pings.Where(p => p.Label != "NITRO5" && p.Label != "NITRO5-WIFI").Select(PingText)));
            sb.Append('\n');
            sb.Append("NITRO5 ").Append(Nitro5Short(s));
            sb.Append('\n');
            sb.Append("PORT  LISTEN ").Append(s.TcpListening).Append(" · ESTAB ").Append(s.TcpEstablished).Append(" · UDP ").Append(s.UdpBound);
            if (s.TopListening.Count > 0) sb.Append(" · ").Append(string.Join(" ", s.TopListening.Select(p => p.Port.ToString(CultureInfo.InvariantCulture))));
            sb.Append('\n');
            sb.Append("đo    NIC ").Append(Format(s.NicMs)).Append(" · ping ").Append(Format(s.PingMs)).Append(" · cổng ").Append(Format(s.PortsMs))
              .Append(" ms · lúc ").Append(LocalTime(s.AtUtc));
            return sb.ToString();
        }

        /// <summary>Right column line under "⏱ Hẹn giờ" (static: changes only when a scan finishes).</summary>
        public static string Nitro5Line(NetRadarSnapshot? s)
        {
            if (s == null) return "⚡ NITRO5 (.22) · chưa dò (/net)";
            if (s.Nitro5Address == null) return $"⚡ NITRO5 · {Nitro5Offline} · {LocalTime(s.AtUtc)}";
            string last = s.Nitro5Address.Substring(s.Nitro5Address.LastIndexOf('.'));
            return $"⚡ NITRO5 ({last}) · {s.Nitro5RttMs} ms · {LocalTime(s.AtUtc)}";
        }

        private static string Nitro5Short(NetRadarSnapshot s) =>
            s.Nitro5Address == null ? Nitro5Offline : $"{s.Nitro5State} {s.Nitro5Address} {s.Nitro5RttMs} ms";

        private static string PingText(NetPingSample p)
        {
            string host = p.Host.StartsWith("192.168.1.", StringComparison.Ordinal) ? p.Host.Substring(p.Host.LastIndexOf('.')) : p.Host;
            return p.Ok ? $"{p.Label} {host} {p.RttMs} ms" : $"{p.Label} {host} OFFLINE";
        }

        private static string LocalTime(string utc) =>
            DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
                ? t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                : utc;

        private static string Format(double ms) => ms.ToString("0.#", CultureInfo.InvariantCulture);

        private static double Ms(long t0) => Math.Round((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, 3);
    }
}
