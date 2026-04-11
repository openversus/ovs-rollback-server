using OVS.Rollback.Interfaces;
using OVS.Rollback.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace OVS.Rollback.Core
{
    public class StatusEventArgs : EventArgs
    {
        public string Description { get; set; }
        public string[] Messages { get; set; }
        public Exception? Exception { get; set; }
        public IMatchStatus StatusObject { get; private set; }
        public StatusEventArgs(string? description = "", string[]? messages = null, IMatchStatus? statusObject = default, Exception? exception = null)
        {
            Description = Utilities.TernaryIsNullOrWhitespace(description, string.Empty);
            Messages = messages ?? Array.Empty<string>();
            StatusObject = statusObject ?? new MatchStatus();
            Exception = exception ?? new Exception();
        }


        public static StatusEventArgs CreateNew(
            string? description = "",
            IEnumerable<string>? messages = null,
            string? matchEvent = "",
            string? matchDescription = "",
            string? matchKey = "",
            string? matchId = "",
            int? matchNumPlayers = 0,
            string? matchPlayerId = "",
            IEnumerable<string>? matchPlayerIds = null,
            Exception? exception = null)
        {
            string[] messagesArray;
            string[] playerIdsArray;

            description = Utilities.TernaryIsNullOrWhitespace(description, string.Empty);

            if (null != messages)
            {
                messages = new List<string>(messages);
                messagesArray = messages.ToArray();
            }
            else
            {
                messagesArray = Array.Empty<string>();
            }

            matchEvent = Utilities.TernaryIsNullOrWhitespace(matchEvent, string.Empty);
            matchDescription = Utilities.TernaryIsNullOrWhitespace(matchDescription, string.Empty);
            matchKey = Utilities.TernaryIsNullOrWhitespace(matchKey, String.Empty);
            matchId = Utilities.TernaryIsNullOrWhitespace(matchId, string.Empty);
            matchNumPlayers = matchNumPlayers ?? 0;

            if (null != matchPlayerIds || matchPlayerIds?.Count() > 0)
            {
                matchPlayerIds = new List<string>(matchPlayerIds);
                playerIdsArray = matchPlayerIds.ToArray();
            }
            else
            {
                playerIdsArray = Array.Empty<string>();
            }

            if (string.IsNullOrWhiteSpace(matchPlayerId))
            {
                if (playerIdsArray.Length == 1)
                {
                    matchPlayerId = playerIdsArray[0];
                }
                else
                {
                    matchPlayerId = string.Empty;
                }
            }

            IMatchStatus statusObject = new MatchStatus
            (
                triggeredEvent: matchEvent,
                description: matchDescription,
                matchId: matchId,
                key: matchKey,
                playerId: matchPlayerId,
                playerIds: playerIdsArray
            );

            return new StatusEventArgs
            (
                description: description,
                messages: messagesArray,
                statusObject: statusObject,
                exception: exception
            );

        }
    }
}
