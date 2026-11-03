using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DiskCleaner
{
    public class ConnectionInfo : Notify
    {
        private string host;
        private long bytesIn = -1, bytesOut = -1, rateIn, rateOut;
        private string state;

        public string Key { get; set; }
        public string Protocol { get; set; }
        public int Pid { get; set; }
        public string AppName { get; set; }
        public string ExePath { get; set; }
        public ImageSource Icon { get; set; }
        public string Local { get; set; }
        public IPAddress RemoteIp { get; set; }
        public int RemotePort { get; set; }
        public bool IsListening { get; set; }

        public string State { get => state; set { state = value; Raise(nameof(State)); } }
        public string Host { get => host; set { host = value; Raise(nameof(Host), nameof(RemoteText)); } }
        public long BytesIn { get => bytesIn; set { bytesIn = value; Raise(nameof(BytesIn), nameof(TrafficText)); } }
        public long BytesOut { get => bytesOut; set { bytesOut = value; Raise(nameof(BytesOut), nameof(TrafficText)); } }
        public long RateIn { get => rateIn; set { rateIn = value; Raise(nameof(RateIn), nameof(RateText)); } }
        public long RateOut { get => rateOut; set { rateOut = value; Raise(nameof(RateOut), nameof(RateText)); } }

        public bool HasIcon => Icon != null;
        public string Service => NetworkMonitor.ServiceName(RemotePort, Protocol);
        public string RemoteText
        {
            get
            {
                if (IsListening) return "Listening on " + Local;
                string addr = RemoteIp.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + RemoteIp + "]" : RemoteIp.ToString();
                string where = string.IsNullOrEmpty(host) ? addr : host + "  (" + addr + ")";
                return where + ":" + RemotePort;
            }
        }
        public string PortText => IsListening ? Protocol : Protocol + "  " + (Service ?? "port " + RemotePort);
        public string TrafficText => bytesIn < 0 ? "" : "↓ " + Fmt.Bytes(bytesIn) + "   ↑ " + Fmt.Bytes(bytesOut);
        public string RateText => bytesIn < 0 ? "" : (rateIn > 0 || rateOut > 0 ? "↓ " + Fmt.Bytes(rateIn) + "/s  ↑ " + Fmt.Bytes(rateOut) + "/s" : "idle");
    }

    public class AppNetworkSummary
    {
        public string Name { get; set; }
        public ImageSource Icon { get; set; }
        public bool HasIcon => Icon != null;
        public int Connections { get; set; }
        public long RateIn { get; set; }
        public long RateOut { get; set; }
        public long TotalIn { get; set; }
        public long TotalOut { get; set; }
        public bool HasTraffic { get; set; }
        public string Text => Connections + (Connections == 1 ? " connection" : " connections")
            + (HasTraffic ? "   ↓ " + Fmt.Bytes(TotalIn) + "  ↑ " + Fmt.Bytes(TotalOut) : "");
    }

    /// <summary>
    /// Lists TCP connections and UDP endpoints with their owning process. When running as administrator it
    /// turns on Windows' per-connection statistics so bytes sent and received can be shown.
    /// Only connection metadata is read; packet contents are never captured.
    /// </summary>
    public class NetworkMonitor
    {
        public class Sample
        {
            public string Key, Protocol, Local, State;
            public int Pid, RemotePort;
            public IPAddress RemoteIp;
            public bool IsListening;
            public long BytesIn = -1, BytesOut = -1;
        }

        private static readonly string[] TcpStates =
            { "", "Closed", "Listening", "Connecting", "Connecting", "Connected", "Closing", "Closing", "Closing", "Closing", "Closing", "Closed (wait)", "Closed" };

        private readonly bool collectBytes;
        private readonly HashSet<string> enabled = new();
        private readonly ConcurrentDictionary<string, string> dns = new();

        public NetworkMonitor(bool isAdmin) { collectBytes = isAdmin; }

        public bool CollectsBytes => collectBytes;

        public List<Sample> Read(bool includeListening)
        {
            var list = new List<Sample>();
            ReadTcp4(list, includeListening);
            ReadTcp6(list, includeListening);
            if (includeListening)
            {
                ReadUdp(list, AF_INET, 12, 0, 4, 8, 4);
                ReadUdp(list, AF_INET6, 28, 0, 20, 24, 16);
            }
            return list;
        }

        // ---- Reverse DNS (cached, never for private addresses) ----

        public string CachedHost(IPAddress ip)
        {
            if (IsLocal(ip)) return IPAddress.IsLoopback(ip) ? "This PC" : "Local network";
            return dns.TryGetValue(ip.ToString(), out string h) ? h : null;
        }

        public async Task<string> ResolveAsync(IPAddress ip)
        {
            string key = ip.ToString();
            if (dns.TryGetValue(key, out string cached)) return cached;
            dns[key] = null; // mark in flight
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                var done = await Task.WhenAny(lookup, Task.Delay(4000));
                string host = done == lookup && lookup.Status == TaskStatus.RanToCompletion ? lookup.Result.HostName : "";
                if (host == key) host = "";
                dns[key] = host;
                return host;
            }
            catch (SocketException)
            {
                dns[key] = "";
                return "";
            }
        }

        public static bool IsLocal(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes();
                return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
            }
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        }

        private static readonly Dictionary<int, string> Services = new()
        {
            [20] = "FTP data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [110] = "POP3",
            [123] = "NTP", [135] = "RPC", [137] = "NetBIOS", [138] = "NetBIOS", [139] = "NetBIOS", [143] = "IMAP", [443] = "HTTPS",
            [445] = "SMB file sharing", [465] = "SMTP (TLS)", [587] = "SMTP", [853] = "DNS over TLS", [993] = "IMAP (TLS)",
            [995] = "POP3 (TLS)", [1433] = "SQL Server", [1900] = "SSDP", [3000] = "Dev server", [3306] = "MySQL", [3389] = "Remote Desktop",
            [3478] = "STUN/TURN", [4000] = "Dev server", [5173] = "Dev server", [5222] = "XMPP", [5228] = "Google services",
            [5353] = "mDNS", [5355] = "LLMNR", [5432] = "PostgreSQL", [6379] = "Redis", [8080] = "HTTP (alt)", [8443] = "HTTPS (alt)",
            [9229] = "Node debugger", [27017] = "MongoDB"
        };

        public static string ServiceName(int port, string protocol)
        {
            if (Services.TryGetValue(port, out string s)) return protocol == "UDP" && port == 443 ? "QUIC (HTTP/3)" : s;
            return null;
        }

        // ---- IP Helper interop ----

        private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;
        private const int STATE_LISTEN = 2, STATE_TIME_WAIT = 11;

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW { public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW
        {
            public uint State;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
            public uint LocalScopeId, LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
            public uint RemoteScopeId, RemotePort;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TCP_ESTATS_DATA_ROD_v0
        {
            public ulong DataBytesOut, DataSegsOut, DataBytesIn, DataSegsIn, SegsOut, SegsIn;
            public uint SoftErrors, SoftErrorReason, SndUna, SndNxt, SndMax;
            public ulong ThruBytesAcked;
            public uint RcvNxt;
            public ulong ThruBytesReceived;
        }

        private const int TcpConnectionEstatsData = 1;

        [DllImport("iphlpapi.dll")]
        private static extern uint SetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, byte[] rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetPerTcpConnectionEStats(ref MIB_TCPROW row, int type, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, out TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

        [DllImport("iphlpapi.dll")]
        private static extern uint SetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, byte[] rw, uint rwVersion, uint rwSize, uint offset);

        [DllImport("iphlpapi.dll")]
        private static extern uint GetPerTcp6ConnectionEStats(ref MIB_TCP6ROW row, int type, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, out TCP_ESTATS_DATA_ROD_v0 rod, uint rodVersion, uint rodSize);

        private static int Port(uint networkOrder) => (int)(((networkOrder & 0xFF) << 8) | ((networkOrder >> 8) & 0xFF));

        private static IntPtr ReadTable(Func<IntPtr, int, (uint rc, int size)> call)
        {
            int size = call(IntPtr.Zero, 0).size;
            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                var (rc, newSize) = call(buf, size);
                if (rc == 0) return buf;
                Marshal.FreeHGlobal(buf);
                if (rc != 122) return IntPtr.Zero;
                size = newSize;
            }
            return IntPtr.Zero;
        }

        private void ReadTcp4(List<Sample> list, bool includeListening)
        {
            IntPtr buf = ReadTable((b, s) => { int sz = s; uint rc = GetExtendedTcpTable(b, ref sz, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0); return (rc, sz); });
            if (buf == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buf + 4 + i * 24;
                    var row = new MIB_TCPROW
                    {
                        State = (uint)Marshal.ReadInt32(p, 0), LocalAddr = (uint)Marshal.ReadInt32(p, 4), LocalPort = (uint)Marshal.ReadInt32(p, 8),
                        RemoteAddr = (uint)Marshal.ReadInt32(p, 12), RemotePort = (uint)Marshal.ReadInt32(p, 16)
                    };
                    int pid = Marshal.ReadInt32(p, 20);
                    var s = MakeTcp((int)row.State, pid, new IPAddress(row.LocalAddr), Port(row.LocalPort), new IPAddress(row.RemoteAddr), Port(row.RemotePort), includeListening);
                    if (s == null) continue;
                    if (collectBytes && row.State == 5) Bytes4(ref row, s);
                    list.Add(s);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void ReadTcp6(List<Sample> list, bool includeListening)
        {
            IntPtr buf = ReadTable((b, s) => { int sz = s; uint rc = GetExtendedTcpTable(b, ref sz, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0); return (rc, sz); });
            if (buf == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buf + 4 + i * 56;
                    var local = new byte[16];
                    var remote = new byte[16];
                    Marshal.Copy(p, local, 0, 16);
                    uint localScope = (uint)Marshal.ReadInt32(p, 16);
                    uint localPort = (uint)Marshal.ReadInt32(p, 20);
                    Marshal.Copy(p + 24, remote, 0, 16);
                    uint remoteScope = (uint)Marshal.ReadInt32(p, 40);
                    uint remotePort = (uint)Marshal.ReadInt32(p, 44);
                    int state = Marshal.ReadInt32(p, 48);
                    int pid = Marshal.ReadInt32(p, 52);
                    var s = MakeTcp(state, pid, new IPAddress(local, localScope), Port(localPort), new IPAddress(remote, remoteScope), Port(remotePort), includeListening);
                    if (s == null) continue;
                    if (collectBytes && state == 5)
                    {
                        var row = new MIB_TCP6ROW
                        {
                            State = (uint)state, LocalAddr = local, LocalScopeId = localScope, LocalPort = localPort,
                            RemoteAddr = remote, RemoteScopeId = remoteScope, RemotePort = remotePort
                        };
                        Bytes6(ref row, s);
                    }
                    list.Add(s);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static Sample MakeTcp(int state, int pid, IPAddress local, int localPort, IPAddress remote, int remotePort, bool includeListening)
        {
            bool listening = state == STATE_LISTEN;
            if (listening && !includeListening) return null;
            if (state == STATE_TIME_WAIT || pid == 0) return null; // already-closed connections that no app owns any more
            if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
            string localText = (local.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + local + "]" : local.ToString()) + ":" + localPort;
            return new Sample
            {
                Protocol = "TCP",
                Pid = pid,
                Local = localText,
                RemoteIp = remote,
                RemotePort = remotePort,
                IsListening = listening,
                State = state < TcpStates.Length ? TcpStates[state] : "?",
                Key = "TCP|" + pid + "|" + localText + "|" + remote + ":" + remotePort
            };
        }

        private void ReadUdp(List<Sample> list, int af, int rowSize, int addrOffset, int portOffset, int pidOffset, int addrLen)
        {
            IntPtr buf = ReadTable((b, s) => { int sz = s; uint rc = GetExtendedUdpTable(b, ref sz, false, af, UDP_TABLE_OWNER_PID, 0); return (rc, sz); });
            if (buf == IntPtr.Zero) return;
            try
            {
                int count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    IntPtr p = buf + 4 + i * rowSize;
                    var addr = new byte[addrLen];
                    Marshal.Copy(p + addrOffset, addr, 0, addrLen);
                    var ip = new IPAddress(addr);
                    int port = Port((uint)Marshal.ReadInt32(p, portOffset));
                    int pid = Marshal.ReadInt32(p, pidOffset);
                    if (pid == 0) continue;
                    string local = (af == AF_INET6 ? "[" + ip + "]" : ip.ToString()) + ":" + port;
                    list.Add(new Sample
                    {
                        Protocol = "UDP",
                        Pid = pid,
                        Local = local,
                        RemoteIp = IPAddress.Any,
                        RemotePort = port,
                        IsListening = true,
                        State = "Open",
                        Key = "UDP|" + pid + "|" + local
                    });
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void Bytes4(ref MIB_TCPROW row, Sample s)
        {
            if (enabled.Add(s.Key))
                SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, new byte[] { 1 }, 0, 1, 0);
            if (GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                    out var rod, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>()) == 0)
            {
                s.BytesIn = (long)rod.DataBytesIn;
                s.BytesOut = (long)rod.DataBytesOut;
            }
        }

        private void Bytes6(ref MIB_TCP6ROW row, Sample s)
        {
            if (enabled.Add(s.Key))
                SetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, new byte[] { 1 }, 0, 1, 0);
            if (GetPerTcp6ConnectionEStats(ref row, TcpConnectionEstatsData, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0,
                    out var rod, 0, (uint)Marshal.SizeOf<TCP_ESTATS_DATA_ROD_v0>()) == 0)
            {
                s.BytesIn = (long)rod.DataBytesIn;
                s.BytesOut = (long)rod.DataBytesOut;
            }
        }
    }
}
