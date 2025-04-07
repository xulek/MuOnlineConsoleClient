namespace MuOnlineConsole;

public class MuOnlineSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ProtocolVersion { get; set; } = "Season6";
    public string ClientVersion { get; set; } = "1.04d";
    public string ClientSerial { get; set; } = "0123456789ABCDEF";
    public Dictionary<byte, byte> DirectionMap { get; set; } = new();
}
