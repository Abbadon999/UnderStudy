using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Steam;

namespace UnderStudy;

/// <summary>
/// Retrieves the bot's owned games together with already-accrued playtime.
/// Primary path uses the bot's own access_token (no Web API key required); falls back to a
/// configured Steam Web API key if provided.
/// </summary>
internal static class OwnedGamesProvider {
	internal sealed record OwnedGame(uint AppID, string? Name, int PlaytimeForever);

	internal static async Task<IReadOnlyList<OwnedGame>?> GetOwnedGames(Bot bot, string? apiKey) {
		ArgumentNullException.ThrowIfNull(bot);

		if (bot.SteamID == 0) {
			return null;
		}

		const string common = "include_appinfo=true&include_played_free_games=true&skip_unvetted_apps=false";

		// Primary: authenticate with the bot's access token.
		string? token = bot.AccessToken;

		if (!string.IsNullOrEmpty(token)) {
			Uri request = new($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?access_token={Uri.EscapeDataString(token)}&steamid={bot.SteamID}&{common}");
			IReadOnlyList<OwnedGame>? games = await Query(request).ConfigureAwait(false);

			if (games is { Count: > 0 }) {
				return games;
			}
		}

		// Fallback: explicit Web API key, if the user supplied one.
		if (!string.IsNullOrEmpty(apiKey)) {
			Uri request = new($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={bot.SteamID}&{common}");

			return await Query(request).ConfigureAwait(false);
		}

		return null;
	}

	private static async Task<IReadOnlyList<OwnedGame>?> Query(Uri request) {
		string? body = await Net.GetString(request).ConfigureAwait(false);

		if (string.IsNullOrEmpty(body)) {
			return null;
		}

		try {
			List<OwnedGameEntry>? games = body.ToJsonObject<OwnedGamesResponse>()?.Response?.Games;

			if (games == null) {
				return null;
			}

			List<OwnedGame> result = [];

			foreach (OwnedGameEntry game in games) {
				if (game.AppId != 0) {
					result.Add(new OwnedGame(game.AppId, game.Name, game.PlaytimeForever));
				}
			}

			return result;
		} catch {
			return null;
		}
	}
}
