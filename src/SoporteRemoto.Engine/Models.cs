using SoporteRemoto.Protocol;

namespace SoporteRemoto.Engine;

public sealed record ConnectionRequest(string ClientName, string RemoteAddress, SessionMode Mode = SessionMode.Screen);

public sealed record FileOffer(int FileId, string FileName, long FileSize);
