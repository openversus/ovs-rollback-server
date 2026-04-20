// Payloads.cs
namespace OVS.Rollback.Interfaces
{
    public interface IMatchStatus
    {
        string Event { get; set; }
        string Description { get; set; }
        string Key { get; set; }
        string MatchId { get; set; }
        int NumPlayers { get; }
        string PlayerId { get; set; }
        string[] PlayerIds { get; }
        ITimeObject Timestamp { get; }

        void AddPlayer(string playerId);
        string ToJson();
    }
}
