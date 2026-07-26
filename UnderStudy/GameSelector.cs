namespace UnderStudy;

/// <summary>Picks which game(s) to play next: favorites, boredom, reunions, plain rotation.</summary>
internal sealed class GameSelector {
	private const int RecentMinKeep = 3;
	private const int RecentMaxKeep = 10;

	private readonly UnderStudyConfig Config;
	private readonly BotState State;
	private readonly GameLibrary Library;
	private readonly Action<string> Log;

	internal GameSelector(UnderStudyConfig config, BotState state, GameLibrary library, Action<string> log) {
		Config = config;
		State = state;
		Library = library;
		Log = log;
	}

	/// <summary>
	/// Most sessions continue the current favorite; sometimes something else for variety;
	/// occasionally the bot gets bored and a new favorite gets adopted afterwards.
	/// </summary>
	internal async Task<List<uint>> PickFavoriteOrGames(int count) {
		if ((Config.ReunionChance > 0) && (Random.Shared.NextDouble() < Config.ReunionChance)) {
			List<uint> capped = [];

			foreach ((uint app, GameEntry entry) in State.Games) {
				if (Library.IsAllowed(app) && (entry.CapMinutes != null) && (Library.Remaining(app) <= 0)) {
					capped.Add(app);
				}
			}

			if (capped.Count > 0) {
				uint revisit = capped[Random.Shared.Next(capped.Count)];
				Log($"Revisiting {Library.Name(revisit)} ({revisit}) for old times' sake.");

				return [revisit];
			}
		}

		if (State.FavoriteApp != 0) {
			uint favorite = State.FavoriteApp;
			bool viable = State.Games.ContainsKey(favorite) && Library.IsAllowed(favorite);

			if (viable) {
				await Library.EnsureCap(favorite).ConfigureAwait(false);
				viable = Library.Remaining(favorite) > 0;
			}

			if (!viable) {
				State.FavoriteApp = 0;
			} else if (Random.Shared.NextDouble() < Config.FavoriteSwitchChance) {
				State.FavoriteApp = 0;
				Log($"Got bored of {Library.Name(favorite)} ({favorite}) — looking for a new favorite.");
			}
		}

		if ((State.FavoriteApp != 0) && (Random.Shared.NextDouble() < Config.FavoriteBias)) {
			List<uint> picks = [State.FavoriteApp];

			if (count > 1) {
				foreach (uint app in await SelectGames(count - 1).ConfigureAwait(false)) {
					if (app != State.FavoriteApp) {
						picks.Add(app);
					}
				}
			}

			return picks;
		}

		List<uint> fresh = await SelectGames(count).ConfigureAwait(false);

		// A successful non-favorite pick while no favorite is set crowns a new favorite.
		if ((Config.FavoriteBias > 0) && (State.FavoriteApp == 0) && (fresh.Count > 0)) {
			State.FavoriteApp = fresh[0];
			Log($"New favorite: {Library.Name(fresh[0])} ({fresh[0]}).");
		}

		return fresh;
	}

	private async Task<List<uint>> SelectGames(int count) {
		List<uint> candidates = [];

		foreach (uint app in State.Games.Keys) {
			if (Library.IsAllowed(app)) {
				candidates.Add(app);
			}
		}

		Rng.Shuffle(candidates);

		List<uint> picks = [];

		// First pass avoids recently played games for variety; second pass allows anything.
		foreach (bool avoidRecent in (bool[]) [true, false]) {
			foreach (uint app in candidates) {
				if (picks.Contains(app)) {
					continue;
				}

				if (avoidRecent && State.Recent.Contains(app)) {
					continue;
				}

				await Library.EnsureCap(app).ConfigureAwait(false);

				if (Library.Remaining(app) > 0) {
					picks.Add(app);

					if (picks.Count >= count) {
						return picks;
					}
				}
			}

			if (picks.Count > 0) {
				break;
			}
		}

		return picks;
	}

	internal void PushRecent(IEnumerable<uint> apps) {
		foreach (uint app in apps) {
			State.Recent.Remove(app);
			State.Recent.Add(app);
		}

		int keep = Math.Max(RecentMinKeep, Math.Min(RecentMaxKeep, State.Games.Count / 2));

		while (State.Recent.Count > keep) {
			State.Recent.RemoveAt(0);
		}
	}
}
