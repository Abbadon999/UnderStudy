using System.Text.Json.Serialization;

namespace UnderStudy;

// Shapes for the two public Steam endpoints this plugin reads. Deserialized through
// ArchiSteamFarm.Helpers.Json.JsonUtilities, so only the fields we actually use are declared —
// unknown members are skipped rather than rejected (see JsonUtilities.DefaultJsonSerializerOptions).

// https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/
internal sealed class OwnedGamesResponse {
	[JsonPropertyName("response")]
	public OwnedGamesResult? Response { get; init; }
}

internal sealed class OwnedGamesResult {
	[JsonPropertyName("games")]
	public List<OwnedGameEntry>? Games { get; init; }
}

internal sealed class OwnedGameEntry {
	[JsonPropertyName("appid")]
	public uint AppId { get; init; }

	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("playtime_forever")]
	public int PlaytimeForever { get; init; }
}

// https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/
internal sealed class AppReviewsResponse {
	[JsonPropertyName("response")]
	public AppReviewsResult? Response { get; init; }
}

internal sealed class AppReviewsResult {
	[JsonPropertyName("reviews")]
	public List<ReviewEntry>? Reviews { get; init; }
}

internal sealed class ReviewEntry {
	[JsonPropertyName("author")]
	public ReviewAuthor? Author { get; init; }
}

internal sealed class ReviewAuthor {
	[JsonPropertyName("playtime_forever")]
	public int PlaytimeForever { get; init; }
}
