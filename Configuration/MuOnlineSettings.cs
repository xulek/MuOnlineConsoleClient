namespace MuOnlineConsole;

public class PacketLoggingSettings
{
    public bool ShowWeather { get; set; } = true;
    public bool ShowDamage { get; set; } = true;
}

public class MuOnlineSettings
{
    // Connect Server Settings
    public string ConnectServerHost { get; set; } = "127.0.0.1"; // Default used if missing in JSON
    public int ConnectServerPort { get; set; } = 44405;       // Default used if missing in JSON

    // Game Server Settings
    public string GameServerHost { get; set; } = "";
    public int GameServerPort { get; set; }

    // Account Settings
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    // Client/Protocol Settings
    public string ProtocolVersion { get; set; } = "Season6";
    public string ClientVersion { get; set; } = "1.04d";
    public string ClientSerial { get; set; } = "0123456789ABCDEF";
    public Dictionary<byte, byte> DirectionMap { get; set; } = new();
    public PacketLoggingSettings PacketLogging { get; set; } = new();
}