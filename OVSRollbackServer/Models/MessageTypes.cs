// MessageTypes.cs
namespace Rollback.Models;

/// <summary>Client → Server message types.</summary>
public enum ClientMessageType : byte
{
    NewConnection = 1,
    Input = 2,
    PlayerInputAck = 3,
    MatchResult = 4,
    QualityData = 5,
    Disconnecting = 6,
    PlayerDisconnectedAck = 7,
    ReadyToStartMatch = 8
}

/// <summary>Server → Client message types.</summary>
public enum ServerMessageType : byte
{
    NewConnectionReply = 1,
    StartGame = 2,
    InputAck = 3,
    PlayerInput = 4,
    RequestQualityData = 6,
    PlayersStatus = 7,
    Kick = 8,
    ChecksumAck = 9,
    PlayersConfigurationData = 10,
    PlayerDisconnected = 11,
    ChangePort = 12
}

