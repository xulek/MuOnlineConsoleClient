namespace MuOnlineConsole;

/// <summary>
/// Marks a method as a packet handler for a specific main and sub code.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PacketHandlerAttribute : Attribute
{
    public byte MainCode { get; }
    public byte SubCode { get; }

    public PacketHandlerAttribute(byte mainCode, byte subCode)
    {
        MainCode = mainCode;
        SubCode = subCode;
    }
}
