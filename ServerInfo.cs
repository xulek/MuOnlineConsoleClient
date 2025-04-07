namespace MuOnlineConsole
{
    /// <summary>
    /// Represents information about a game server received from the Connect Server.
    /// </summary>
    public class ServerInfo
    {
        public ushort ServerId { get; set; }
        public byte LoadPercentage { get; set; }
        // Add other relevant fields if needed, e.g., ServerName, IsOnline, etc.
        // For now, we just need ID and Load for selection.

        public override string ToString()
        {
            // Basic representation, can be enhanced
            return $"Server ID: {ServerId}, Load: {LoadPercentage}%";
        }
    }
}