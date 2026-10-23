using ArchiSteamFarm.Steam;

namespace UnderStudy;

/// <summary>Owned-games bookkeeping: eligibility, playtime caps, and refreshing from Steam.</summary>
internal sealed class GameLibrary {
	// A computed cap is trusted for two weeks; one still on the DefaultCapMinutes fallback (no
	// review data yet) is retried after a day so a transient fetch failure doesn't stick for long.
	private const int CapRefreshDays = 14;
	private const int CapRetryDays = 1;

	// Per-game randomization applied on top of the playtime cap.
	private const double JitterMin = 0.85;
	private const double JitterMax = 1.15;

	private readonly Bot Bot;
	private readonly UnderStudyConfig Config;
	private readonly BotState State;
	private readonly Action<string> Log;

	internal GameLibrary(Bot bot, UnderStudyConfig config, BotState state, Action<string> log) {
		Bot = bot;
		Config = config;
		State = state;
		Log = log;
	}

	internal bool IsAllowed(uint app) {
		if (Config.BlacklistAppIDs.Contains(app)) {
			return false;
		}

		return (Config.IncludeAppIDs.Count == 0) || Config.IncludeAppIDs.Contains(app);
	}

	internal int Remaining(uint app) {
		GameEntry entry = State.Games[app];
		int cap = entry.CapMinutes ?? Config.DefaultCapMinutes;

		return cap - entry.PlaytimeForever;
	}

	internal async Task EnsureCap(uint app) {
		GameEntry entry = State.Games[app];
		bool stale = entry.SteamSpyFetchedUtc < DateTime.UtcNow.AddDays(entry.PlaytimeMetric == null ? -CapRetryDays : -CapRefreshDays);

		if ((entry.CapMinutes != null) && !stale) {
			return;
		}

		if (stale || (entry.PlaytimeMetric == null)) {
			entry.PlaytimeMetric = await SteamReviewsClient.GetPlaytimeMinutes(app, Config.UseAverage).ConfigureAwait(false) ?? entry.PlaytimeMetric;
			entry.SteamSpyFetchedUtc = DateTime.UtcNow;
		}

		if (entry.Jitter <= 0) {
			entry.Jitter = JitterMin + (Random.Shared.NextDouble() * (JitterMax - JitterMin));
		}

		double basis = entry.PlaytimeMetric ?? Config.DefaultCapMinutes;
		entry.CapMinutes = Math.Max(1, (int) Math.Round(basis * Config.PlaytimeFactor * entry.Jitter));
	}

	internal uint MostPlayedOwned() {
		uint best = 0;
		int bestPlay = -1;

		foreach ((uint app, GameEntry entry) in State.Games) {
			if (IsAllowed(app) && (entry.PlaytimeForever > bestPlay)) {
				best = app;
				bestPlay = entry.PlaytimeForever;
			}
		}

		return best;
	}

	internal string Name(uint app) => State.Games.TryGetValue(app, out GameEntry? entry) && !string.IsNullOrEmpty(entry.Name) ? entry.Name : app.ToString();

	internal async Task RefreshOwnedIfNeeded(bool force) {
		if (!force && (DateTime.UtcNow - State.LastOwnedRefreshUtc < TimeSpan.FromMinutes(Config.RefreshOwnedMinutes))) {
			return;
		}

		IReadOnlyList<OwnedGamesProvider.OwnedGame>? games = await OwnedGamesProvider.GetOwnedGames(Bot, Config.SteamApiKey).ConfigureAwait(false);

		if (games == null) {
			Log("Could not fetch owned games (access token / API key). Will retry later.");

			return;
		}

		foreach (OwnedGamesProvider.OwnedGame game in games) {
			if (!IsAllowed(game.AppID)) {
				continue;
			}

			if (!State.Games.TryGetValue(game.AppID, out GameEntry? entry)) {
				entry = new GameEntry();
				State.Games[game.AppID] = entry;
			}

			entry.Name = game.Name ?? entry.Name;
			entry.PlaytimeForever = game.PlaytimeForever; // Steam is the source of truth.
		}

		State.LastOwnedRefreshUtc = DateTime.UtcNow;
		Log($"Refreshed library: {State.Games.Count} eligible games.");

		// Prefetch caps for the whole library so "list" and picks reflect real data immediately;
		// EnsureCap no-ops for anything already fresh.
		foreach (uint app in State.Games.Keys) {
			await EnsureCap(app).ConfigureAwait(false);
		}
	}
}
