using System.Net;
using System.Reflection;
using System.Text.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace Dimensions;

[ApiVersion(2, 1)]
public sealed class Plugin : TerrariaPlugin
{
    public override string Name => "Dimensions";
    public override string Author => "popstarfreas / REXT adaptation";
    public override string Description => "Applies Dimensions RealIP from a trusted proxy to TShock.IP.";
    public override Version Version => new(1, 6, 0);
    private readonly object gate = new();
    private readonly Dictionary<int, Pending> pending = new();
    private HashSet<string> trusted = new();
    private FieldInfo? cache;
    private bool enabled;
    private sealed class Pending(object socket, string ip)
    {
        public object Socket = socket;
        public string Ip = ip;
        public TSPlayer? AppliedTo;
    }
    public sealed class Settings
    {
        public bool EnableGeoIP { get; set; } = false;
        public string[] TrustedProxyIPs { get; set; } = ["127.0.0.1", "::1"];
    }
    public Plugin(Main game) : base(game) { }
    public override void Initialize()
    {
        var file = Path.Combine(TShock.SavePath, "Dimensions.json");
        if (!File.Exists(file))
            File.WriteAllText(file, JsonSerializer.Serialize(new Settings(), new JsonSerializerOptions { WriteIndented = true }));
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? throw new InvalidDataException("Invalid RextRealIp config");
        if (settings.EnableGeoIP) TShock.Log.ConsoleWarn("[Dimensions] Legacy EnableGeoIP is not supported by this receiver; GeoIP lookup is disabled.");
        trusted = settings.TrustedProxyIPs.Select(x => Normalize(IPAddress.Parse(x))).ToHashSet(StringComparer.Ordinal);
        cache = typeof(TSPlayer).GetField("CacheIP", BindingFlags.Instance | BindingFlags.NonPublic);
        if (cache?.FieldType != typeof(string))
            throw new NotSupportedException("TShock CacheIP field not found. Receiver disabled; no success acknowledgement will be sent.");
        enabled = true;
        ServerApi.Hooks.NetGetData.Register(this, OnData, int.MaxValue);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
        TShock.Log.ConsoleInfo("[REXT RealIP] Loaded. Configure TrustedProxyIPs with the physical address of your proxy.");
    }
    private static string Normalize(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    private void OnLeave(LeaveEventArgs args) { lock (gate) pending.Remove(args.Who); }
    private void OnData(GetDataEventArgs args)
    {
        if (!enabled || args.Handled) return;
        int slot = args.Msg.whoAmI;
        if (slot < 0 || slot >= TShock.Players.Length) return;
        var socket = Netplay.Clients[slot]?.Socket;
        if (socket is null) return;
        lock (gate)
        {
            if ((int)args.MsgID == 67)
            {
                // Claim only subtype 0, not unrelated Dimensions control packets.
                if (args.Length < 2 || args.Index < 0 || args.Index + args.Length > args.Msg.readBuffer.Length) return;
                var data = args.Msg.readBuffer;
                if (data[args.Index] != 0 || data[args.Index + 1] != 0) return;
                args.Handled = true;
                var physical = socket.GetRemoteAddress()?.GetIdentifier();
                if (!IPAddress.TryParse(physical, out var peer) || !trusted.Contains(Normalize(peer))) return;
                try
                {
                    if (args.Length > 128) return;
                    using var ms = new MemoryStream(data, args.Index, args.Length, false);
                    using var reader = new BinaryReader(ms);
                    if (reader.ReadUInt16() != 0) return;
                    string text = reader.ReadString();
                    if (ms.Position != ms.Length || !IPAddress.TryParse(text, out var address)) return;
                    string ip = Normalize(address);
                    if (pending.TryGetValue(slot, out var old) && ReferenceEquals(old.Socket, socket))
                    {
                        // A single connection cannot switch identity later.
                        if (old.Ip != ip) return;
                    }
                    else pending[slot] = new Pending(socket, ip);
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is FormatException) { return; }
            }
            if (!pending.TryGetValue(slot, out var state) || !ReferenceEquals(state.Socket, socket)) return;
            var player = TShock.Players[slot];
            if (player is null || ReferenceEquals(state.AppliedTo, player)) return;
            cache!.SetValue(player, state.Ip);
            if (player.IP != state.Ip)
            {
                TShock.Log.ConsoleError("[REXT RealIP] TShock did not accept the IP; no acknowledgement sent.");
                return;
            }
            state.AppliedTo = player;
            TShock.Log.ConsoleInfo($"[REXT RealIP] slot={slot}, applied={player.IP}");
            using var ack = new MemoryStream();
            using var writer = new BinaryWriter(ack);
            writer.Write((ushort)0); writer.Write((byte)67); writer.Write((ushort)0x5258); writer.Write(state.Ip);
            writer.Flush(); ack.Position = 0; writer.Write((ushort)ack.Length); writer.Flush();
            player.SendRawData(ack.ToArray());
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && enabled)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnData);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
            lock (gate) pending.Clear();
        }
        base.Dispose(disposing);
    }
}
