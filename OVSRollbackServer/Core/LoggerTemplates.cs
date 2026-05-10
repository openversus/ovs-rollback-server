using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Core
{
    public sealed partial class LoggerTemplates
    {
        public static partial class Log
        {
            // ── Server Lifecycle ──

            [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
                Message = "{callerName}: Listening on UDP port {Port}")]
            public static partial void Listening(ILogger logger, ushort port, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
                Message = "{callerName}: OVS server started ({ServerType} on port: {port})")]
            public static partial void ServerStarted(ILogger logger, string serverType, ushort port, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
                Message = "{callerName}: OVS server stopped")]
            public static partial void ServerStopped(ILogger logger, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
                Message = "{callerName}: Server running. Press Ctrl+C to stop.")]
            public static partial void ServerRunning(ILogger logger, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
                Message = "{callerName}: Shutting down server...")]
            public static partial void ShuttingDown(ILogger logger, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
                Message = "{callerName}: Match data endpoint: {baseURL}")]
            public static partial void MatchEndpoint(ILogger logger, string baseURL, [CallerMemberName] string callerName = "");

            // ── Match Lifecycle ──

            [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
                Message = "{callerName}: New Match: {MatchId}")]
            public static partial void NewMatch(ILogger logger, string matchId, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
                Message = "{callerName}: Match {MatchId} cleaned up (all players disconnected)")]
            public static partial void MatchCleanedUp(ILogger logger, string matchId, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
                Message = "{callerName}: Sent end match notice for Match ID {matchID} to URL: {url}")]
            public static partial void MatchEnded(ILogger logger, string matchId, string url, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
                Message = "{callerName}: Received connection from IP address: {ip}")]
            public static partial void ConnectionReceived(ILogger logger, string ip, [CallerMemberName] string callerName = "");

            // ── Player Lifecycle ──

            [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
                Message = "{callerName}: Player {PlayerIndex} (ID: {playerId} Name: {playerName} Character: {playerCharacter} joined match {matchID}")]
            public static partial void PlayerJoined(
                ILogger logger,
                ushort playerIndex,
                string playerId,
                string playerName,
                string playerCharacter,
                string matchID,
                [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
                Message = "{callerName}: Player index {PlayerIndex} for matchID {matchID} timed out (no input for {Timeout}s)")]
            public static partial void PlayerTimedOut(ILogger logger, ushort playerIndex, string matchID, int timeout, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
                Message = "{callerName}: Player index {PlayerIndex} sent Disconnecting message for Match ID: {matchID}")]
            public static partial void PlayerDisconnecting(ILogger logger, ushort playerIndex, string matchID, [CallerMemberName] string callerName = "");

            // ── Ping Phase ──

            [LoggerMessage(EventId = 1300, Level = LogLevel.Information,
                Message = "{callerName}: Starting ping phase for Match ID: {matchID}")]
            public static partial void PingPhaseStarted(ILogger logger, string matchID, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 1301, Level = LogLevel.Information,
                Message = "{callerName}: Broadcasting players configuration for match {matchID}")]
            public static partial void BroadcastingPlayersConfig(ILogger logger, string matchID, [CallerMemberName] string callerName = "");

            // ── Rift ──

            [LoggerMessage(EventId = 1400, Level = LogLevel.Information,
                Message = "{callerName}: MatchID: {matchID} PIndex:{PlayerIndex} PING:{Ping} RIFT:{SmoothRift:F2} RAWRIFT:{RawRift:F2} clientFrame:{ClientFrame:F1} serverFrame:{ServerFrame}")]
            public static partial void RiftInfo(ILogger logger, string matchID, ushort playerIndex,
                short ping, float smoothRift, float rawRift, float clientFrame, uint serverFrame, [CallerMemberName] string callerName = "");

            // ── Tick Performance ──

            [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
                Message = "{callerName}: Average tick interval: {AvgUs:F0} μs")]
            public static partial void TickPerformance(ILogger logger, double AvgUs, [CallerMemberName] string callerName = "");

            // ── Warnings ──

            [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
                Message = "{callerName}: DESYNC at frame {Frame}: player (name: {PlayerNameA}) {PlayerA}={ChecksumA} vs player (name: {PlayerNameB}) {PlayerB}={ChecksumB}")]
            public static partial void DesyncDetected(ILogger logger, uint frame,
                ushort playerA, string? PlayerNameA, string checksumA, ushort playerB, string PlayerNameB, string checksumB, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
                Message = "{callerName}: Bit-packing fallback: {Reason}")]
            public static partial void BitPackingFallback(ILogger logger, string reason, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
                Message = "{callerName}: OVS_SERVER not set, checking mvsi_server")]
            public static partial void OVSNotSet(ILogger logger, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
                Message = "{callerName}: Neither OVS_SERVER nor mvsi_server set")]
            public static partial void NoServerConfigured(ILogger logger, [CallerMemberName] string callerName = "");

            // ── Errors ──

            [LoggerMessage(EventId = 3000, Level = LogLevel.Error,
                Message = "{callerName}: UDP receive error: ")]
            public static partial void ReceiveError(ILogger logger, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
                Message = "{callerName}: Error handling message: ")]
            public static partial void HandleError(ILogger logger, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3002, Level = LogLevel.Error,
                Message = "{callerName}: Send failed to player {PlayerIndex} for MatchID {matchID}: ")]
            public static partial void SendFailed(ILogger logger, ushort playerIndex, string matchID, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3003, Level = LogLevel.Error,
                Message = "{callerName}: Failed to fetch match config for match: {matchID}")]
            public static partial void FetchConfigFailed(ILogger logger, string matchID, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
                Message = "{callerName}: Ping phase error for Match ID {matchID}: ")]
            public static partial void PingPhaseError(ILogger logger, string matchID, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
                Message = "{callerName}: Failed to POST end-match to {Url}, exception: ")]
            public static partial void EndMatchFailed(ILogger logger, string url, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
                Message = "{callerName}: Invalid JSON from {Path}")]
            public static partial void InvalidJson(ILogger logger, string path, Exception exception, [CallerMemberName] string callerName = "");

            [LoggerMessage(EventId = 3007, Level = LogLevel.Error,
                Message = "{callerName}: Failed to post match status event \"{Event}\" for MatchID {matchID}, error: ")]
            public static partial void SendMatchStatusFailed(ILogger logger, string Event, string matchID, Exception exception, [CallerMemberName] string callerName = "");

        }
    }
}
