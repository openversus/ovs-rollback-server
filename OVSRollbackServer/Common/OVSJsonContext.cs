// OVSJsonContext.cs
using OVS.Rollback.Configuration;
using OVS.Rollback.Interfaces;
using OVS.Rollback.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OVS.Rollback.Common
{
    /// <summary>
    /// Source-generated System.Text.Json metadata for every JSON shape the server reads or
    /// writes (config file, matchmaker HTTP payloads, diagnostic logging). Source generation
    /// avoids reflection-based serializer warmup at runtime and keeps the codebase
    /// trim/AOT-compatible.
    /// </summary>
    /// <remarks>
    /// The read-side options mirror Newtonsoft.Json's permissive defaults that the config
    /// loader previously relied on (case-insensitive property matching, comments and trailing
    /// commas tolerated, numbers readable from strings). None of these options affect
    /// serialized output, so the JSON sent to the matchmaker is unchanged.
    /// </remarks>
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(ServerConfiguration))]
    [JsonSerializable(typeof(OVSMatchConfig))]
    [JsonSerializable(typeof(MatchStatusResponse))]
    [JsonSerializable(typeof(RegisterPayload))]
    [JsonSerializable(typeof(EndMatchPayload))]
    [JsonSerializable(typeof(MatchStatus))]
    [JsonSerializable(typeof(ITimeObject))]
    [JsonSerializable(typeof(NewConnectionPayload))]
    public partial class OVSJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Source-generated metadata for the snake_case, indented JSON produced by
    /// <see cref="MatchStatus.ToJson"/> and <see cref="TimeObject.ToJSON"/>. Options match the
    /// inline <see cref="JsonSerializerOptions"/> those methods constructed previously.
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        IndentSize = 4,
        NewLine = "\n",
        MaxDepth = 10,
        IncludeFields = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(MatchStatus))]
    [JsonSerializable(typeof(TimeObject))]
    [JsonSerializable(typeof(ITimeObject))]
    public partial class SnakeCaseJsonContext : JsonSerializerContext
    {
    }
}
