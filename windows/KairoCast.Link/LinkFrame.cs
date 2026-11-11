namespace KairoCast.Link;

public enum LinkType : byte { Hello = 1, Video = 2, Audio = 3, Bye = 4, KeyReq = 5, Ping = 6, Status = 7 }

public class LinkProtocolException : Exception
{
    public LinkProtocolException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record LinkFrame(LinkType Type, int Flags, long PtsUs, byte[] Payload)
{
    public bool IsKey => (Flags & 1) != 0;
}
