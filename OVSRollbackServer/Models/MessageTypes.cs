// MessageTypes.cs
using System;
namespace OVS.Rollback.Models
{
    /// <summary>Client → Server message types.</summary>
    /// <remarks>
    /// Checked against the game client's serializer (0x1412173c0 in the final build), which has
    /// cases for types 1..9. Type 7 is absent here on purpose: the client's case for it writes
    /// no payload, and nothing in the client ever builds one.
    /// </remarks>
    public enum ClientMessageType : byte
    {
        NewConnection = 1,
        Input = 2,
        PlayerInputAck = 3,
        MatchResult = 4,
        QualityData = 5,
        Disconnecting = 6,
        ReadyToStartMatch = 8,
        /// <summary>
        /// The client's automatic reply to <see cref="ServerMessageType.PlayerDisconnected"/>:
        /// one byte, the low byte of that message's PlayerDisconnectedArrayIndex.
        /// </summary>
        PlayerDisconnectedAck = 9
    }

    /// <summary>Server → Client message types.</summary>
    public enum ServerMessageType : byte
    {
        NewConnectionReply = 1,
        StartGame = 2,
        InputAck = 3,
        PlayerInput = 4,
        /// <summary>
        /// No payload. Moves a client whose match has ended locally (session state 7, where it
        /// sends MatchResult every tick) to state 8, after which it sends Disconnecting (reason 1)
        /// and stops. The server does not send it yet.
        /// </summary>
        EndOfMatchAck = 5,
        RequestQualityData = 6,
        PlayersStatus = 7,
        Kick = 8,
        ChecksumAck = 9,
        PlayersConfigurationData = 10,
        PlayerDisconnected = 11,
        ChangePort = 12
    }
}
