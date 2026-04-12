using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace OVS.Rollback.Core
{
    public sealed partial class LoggerTemplates
    {
        public static partial class Log
        {
            // ── Server Lifecycle ──

            [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
                Message = "Listening on UDP port {Port}")]
            public static partial void Listening(ILogger logger, ushort port);

            [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
                Message = "OVS server started ({ServerType} on port: {port})")]
            public static partial void ServerStarted(ILogger logger, string serverType, ushort port);

            [LoggerMessage(EventId = 1002, Level = LogLevel.Information,
                Message = "OVS server stopped")]
            public static partial void ServerStopped(ILogger logger);

            [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
                Message = "Server running. Press Ctrl+C to stop.")]
            public static partial void ServerRunning(ILogger logger);

            [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
                Message = "Shutting down server...")]
            public static partial void ShuttingDown(ILogger logger);

            [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
                Message = "Match data endpoint: {baseURL}")]
            public static partial void MatchEndpoint(ILogger logger, string baseURL);

            // ── Match Lifecycle ──

            [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
                Message = "New Match: {MatchId}")]
            public static partial void NewMatch(ILogger logger, string matchId);

            [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
                Message = "Match {MatchId} cleaned up (all players disconnected)")]
            public static partial void MatchCleanedUp(ILogger logger, string matchId);

            [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
                Message = "Sent end match notice for Match ID {matchID} to URL: {url}")]
            public static partial void MatchEnded(ILogger logger, string matchId, string url);

            [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
                Message = "Received connection from IP address: {ip}")]
            public static partial void ConnectionReceived(ILogger logger, string ip);

            // ── Player Lifecycle ──

            [LoggerMessage(EventId = 1200, Level = LogLevel.Information,
                Message = "Player {PlayerIndex} joined match {matchID}")]
            public static partial void PlayerJoined(ILogger logger, ushort playerIndex, string matchID);

            [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
                Message = "Player index {PlayerIndex} for matchID {matchID} timed out (no input for {Timeout}s)")]
            public static partial void PlayerTimedOut(ILogger logger, ushort playerIndex, string matchID, int timeout);

            [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
                Message = "Player index {PlayerIndex} sent Disconnecting message for Match ID: {matchID}")]
            public static partial void PlayerDisconnecting(ILogger logger, ushort playerIndex, string matchID);

            // ── Ping Phase ──

            [LoggerMessage(EventId = 1300, Level = LogLevel.Information,
                Message = "Starting ping phase for Match ID: {matchID}")]
            public static partial void PingPhaseStarted(ILogger logger, string matchID);

            [LoggerMessage(EventId = 1301, Level = LogLevel.Information,
                Message = "Broadcasting players configuration for match {matchID}")]
            public static partial void BroadcastingPlayersConfig(ILogger logger, string matchID);

            // ── Rift ──

            [LoggerMessage(EventId = 1400, Level = LogLevel.Information,
                Message = "MatchID: {matchID} PIndex:{PlayerIndex} PING:{Ping} RIFT:{SmoothRift:F2} RAWRIFT:{RawRift:F2} clientFrame:{ClientFrame:F1} serverFrame:{ServerFrame}")]
            public static partial void RiftInfo(ILogger logger, string matchID, ushort playerIndex,
                short ping, float smoothRift, float rawRift, float clientFrame, uint serverFrame);

            // ── Tick Performance ──

            [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
                Message = "Average tick interval: {AvgUs:F0} μs")]
            public static partial void TickPerformance(ILogger logger, double AvgUs);

            // ── Warnings ──

            [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
                Message = "DESYNC at frame {Frame}: player {PlayerA}={ChecksumA} vs player {PlayerB}={ChecksumB}")]
            public static partial void DesyncDetected(ILogger logger, uint frame,
                ushort playerA, string checksumA, ushort playerB, string checksumB);

            [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
                Message = "Bit-packing fallback: {Reason}")]
            public static partial void BitPackingFallback(ILogger logger, string reason);

            [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
                Message = "OVS_SERVER not set, checking mvsi_server")]
            public static partial void OVSNotSet(ILogger logger);

            [LoggerMessage(EventId = 2003, Level = LogLevel.Warning,
                Message = "Neither OVS_SERVER nor mvsi_server set")]
            public static partial void NoServerConfigured(ILogger logger);

            // ── Errors ──

            [LoggerMessage(EventId = 3000, Level = LogLevel.Error,
                Message = "UDP receive error: ")]
            public static partial void ReceiveError(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 3001, Level = LogLevel.Error,
                Message = "Error handling message: ")]
            public static partial void HandleError(ILogger logger, Exception exception);

            [LoggerMessage(EventId = 3002, Level = LogLevel.Error,
                Message = "Send failed to player {PlayerIndex} for MatchID {matchID}: ")]
            public static partial void SendFailed(ILogger logger, ushort playerIndex, string matchID, Exception exception);

            [LoggerMessage(EventId = 3003, Level = LogLevel.Error,
                Message = "Failed to fetch match config for match: {matchID}")]
            public static partial void FetchConfigFailed(ILogger logger, string matchID, Exception exception);

            [LoggerMessage(EventId = 3004, Level = LogLevel.Error,
                Message = "Ping phase error for Match ID {matchID}: ")]
            public static partial void PingPhaseError(ILogger logger, string matchID, Exception exception);

            [LoggerMessage(EventId = 3005, Level = LogLevel.Error,
                Message = "Failed to POST end-match to {Url}, exception: ")]
            public static partial void EndMatchFailed(ILogger logger, string url, Exception exception);

            [LoggerMessage(EventId = 3006, Level = LogLevel.Error,
                Message = "Invalid JSON from {Path}")]
            public static partial void InvalidJson(ILogger logger, string path);
        }
    }
}
