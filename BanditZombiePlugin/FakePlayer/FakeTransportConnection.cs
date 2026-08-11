using System;
using System.Net;
using SDG.NetTransport;

namespace BanditZombiePlugin.FakePlayer
{
    /// <summary>
    /// Minimal no-op ITransportConnection so Provider.addPlayer() has something non-null to register
    /// for a bot with no real client on the other end. Confirmed via decompiling the real
    /// SDG.NetTransport.dll that the interface is only 7 members; confirmed via decompiling
    /// Provider.addPlayer() that it doesn't do anything transport-specific with this beyond storing
    /// it as a dictionary key and later calling Send()/CloseConnection() on it - both safe as no-ops
    /// here since nobody is listening on the other end.
    /// </summary>
    public sealed class FakeTransportConnection : ITransportConnection
    {
        public bool TryGetIPv4Address(out uint address)
        {
            address = 0;
            return false;
        }

        public bool TryGetPort(out ushort port)
        {
            port = 0;
            return false;
        }

        public bool TryGetSteamId(out ulong steamId)
        {
            steamId = 0;
            return false;
        }

        public IPAddress GetAddress() => IPAddress.Loopback;

        public string GetAddressString(bool withPort) => "fake-bot-connection";

        public void CloseConnection()
        {
            // No real socket to close.
        }

        public void Send(byte[] buffer, long size, ENetReliability reliability)
        {
            // No-op: nobody is listening on the other end of this "connection".
        }

        public bool Equals(ITransportConnection other) => ReferenceEquals(this, other);

        public override bool Equals(object obj) => ReferenceEquals(this, obj);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}
