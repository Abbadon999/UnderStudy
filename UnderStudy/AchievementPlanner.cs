using ArchiSteamFarm.Steam;

namespace UnderStudy;

/// <summary>
/// Achievement pacing and unlocking, delegated to ASFAchievementManager through its command bridge
/// (<c>Bot.Commands.Response("alist ...")</c> / <c>"aset ..."</c>). <see cref="Plan"/> schedules
/// unlock moments for a session; the scheduler polls <see cref="TryTakeDueUnlock"/> each tick and
/// calls <see cref="FireNext"/> once one is due.
/// </summary>
internal sealed class AchievementPlanner {
	// Roll thresholds for how many unlocks a session gets: 25% zero, 55% one, 20% two.
	private const double ZeroUnlockRollCeiling = 0.25;
	private const double OneUnlockRollCeiling = 0.80;
	private const int ShortSessionMinutes = 10;

	private readonly Bot Bot;
	private readonly UnderStudyConfig Config;
	private readonly BotState State;
	private readonly GameLibrary Library;
	private readonly Action<string> Log;

	// In-memory only: a restart mid-session drops any planned unlock instead of persisting it.
	private readonly List<DateTime> PlannedUnlocks = [];
	private uint PlannedUnlockApp;

	internal AchievementPlanner(Bot bot, UnderStudyConfig config, BotState state, GameLibrary library, Action<string> log) {
		Bot = bot;
		Config = config;
		State = state;
		Library = library;
		Log = log;
	}

	internal void Plan(uint app, int sessionMinutes) {
		PlannedUnlocks.Clear();
		PlannedUnlockApp = app;

		AchievementsConfig cfg = Config.Achievements;

		if (!cfg.Enabled || (cfg.MaxPerSession < 1)) {
			return;
		}

		if (!State.Games.TryGetValue(app, out GameEntry? entry) || (entry.AchTarget == 0)) {
			return;
		}

		if (GiveUpIfProtected(app, entry)) {
			return;
		}

		// Already at the personal target (when the counts are known) — nothing to plan.
		if ((entry.AchTarget is int target) && (entry.AchTotal is int total) && (total > 0)) {
			int targetCount = Math.Max(1, ((total * target) + 99) / 100);

			if (entry.AchAchieved >= targetCount) {
				return;
			}
		}

		double roll = Random.Shared.NextDouble();
		int count = roll < ZeroUnlockRollCeiling ? 0 : roll < OneUnlockRollCeiling ? 1 : Math.Min(2, cfg.MaxPerSession);

		if (sessionMinutes < ShortSessionMinutes) {
			count = Math.Min(count, 1);
		}

		for (int i = 0; i < count; i++) {
			int minute = Rng.Between(2, Math.Max(2, sessionMinutes - 2));
			DateTime when = DateTime.UtcNow.AddMinutes(minute);

			// Insert keeping the list sorted ascending.
			int at = PlannedUnlocks.Count;

			while ((at > 0) && (PlannedUnlocks[at - 1] > when)) {
				at--;
			}

			PlannedUnlocks.Insert(at, when);
		}
	}

	/// <summary>True if a planned unlock for the currently-playing app is due; consumes it if so.</summary>
	internal bool TryTakeDueUnlock(IReadOnlyList<uint> currentApps, out uint app) {
		app = PlannedUnlockApp;

		if ((PlannedUnlocks.Count == 0) || (DateTime.UtcNow < PlannedUnlocks[0]) || !currentApps.Contains(PlannedUnlockApp)) {
			return false;
		}

		PlannedUnlocks.RemoveAt(0);

		return true;
	}

	internal async Task FireNext(uint app) {
		if (!State.Games.TryGetValue(app, out GameEntry? entry)) {
			return;
		}

		string? listResponse = await Bot.Commands.Response(EAccess.Master, $"alist {Bot.BotName} {app}").ConfigureAwait(false);

		if (string.IsNullOrEmpty(listResponse)) {
			return;
		}

		List<AchRow> rows = ParseAchievementList(listResponse);

		int total = rows.Count;
		int achieved = 0;
		List<int> locked = [];

		foreach (AchRow row in rows) {
			if (row.Achieved) {
				achieved++;
			} else {
				locked.Add(row.Index);
			}
		}

		entry.AchTotal = total;
		entry.AchAchieved = achieved;

		if (total == 0) {
			// No achievements in this game — never look at it again.
			entry.AchTarget = 0;
			PlannedUnlocks.Clear();

			return;
		}

		// If the previous attempt did not stick, the achievement is protected — never retry it.
		// If it did stick, this is the moment it is confirmed as genuinely unlocked.
		if (entry.AchLastTry is int lastTry) {
			entry.AchLastTry = null;

			if (locked.Contains(lastTry)) {
				if (!entry.AchFailed.Contains(lastTry)) {
					entry.AchFailed.Add(lastTry);
					Log($"Achievement #{lastTry} in {Library.Name(app)} appears protected — won't retry.");
				}
			} else {
				Log($"Unlocked achievement #{lastTry} in {Library.Name(app)} — confirmed ({achieved}/{total} achieved).");
			}
		}

		if (GiveUpIfProtected(app, entry)) {
			PlannedUnlocks.Clear();

			return;
		}

		if (entry.AchTarget == null) {
			if (Random.Shared.NextDouble() < Config.Achievements.SkipGameChance) {
				entry.AchTarget = 0;
				Log($"{Library.Name(app)}: not bothering with achievements in this one.");

				return;
			}

			entry.AchTarget = Rng.Between(Config.Achievements.MinTargetPercent, Config.Achievements.MaxTargetPercent);
			Log($"{Library.Name(app)}: personal achievement target set to {entry.AchTarget}%.");
		}

		if (entry.AchTarget is not int pct || (pct <= 0)) {
			return;
		}

		int targetTotal = Math.Max(1, ((total * pct) + 99) / 100);

		if (achieved >= targetTotal) {
			PlannedUnlocks.Clear();

			return;
		}

		// Pace unlocks across the game's planned lifetime: the k-th achievement needs roughly
		// k/target of the playtime cap spent, so early hours can't produce late-game trophies.
		int capMinutes = entry.CapMinutes ?? Config.DefaultCapMinutes;
		double progress = Math.Min(1.0, entry.PlaytimeForever / (double) Math.Max(1, capMinutes));
		int allowedNow = ((int) (progress * targetTotal)) + 1;

		if (achieved >= allowedNow) {
			return;
		}

		List<AchRow> candidates = [];

		foreach (AchRow row in rows) {
			if (!row.Achieved && !entry.AchFailed.Contains(row.Index)) {
				candidates.Add(row);
			}
		}

		if (candidates.Count == 0) {
			return;
		}

		candidates = await RankByBelievability(app, candidates, progress).ConfigureAwait(false);

		if (candidates.Count == 0) {
			return;
		}

		// Occasionally skip the front-runner so the pick isn't always the deterministic "safest" one.
		int pick = candidates[0].Index;

		if ((candidates.Count > 1) && (Random.Shared.NextDouble() < Config.Achievements.SkipFrontRunnerChance)) {
			pick = candidates[1].Index;
		}

		entry.AchLastTry = pick;
		await Bot.Commands.Response(EAccess.Master, $"aset {Bot.BotName} {app} {pick}").ConfigureAwait(false);
		Log($"Trying achievement #{pick} in {Library.Name(app)} ({achieved + 1}/{targetTotal} toward personal target).");
	}

	// Orders candidates by believability: common (high global unlock %) first, "finish the
	// game"-style achievements last and only once most of the playtime cap is spent. Falls back to
	// the original (schema) order when community stats are unavailable.
	private async Task<List<AchRow>> RankByBelievability(uint app, List<AchRow> candidates, double progress) {
		List<GlobalStatsClient.AchStat>? stats = await GlobalStatsClient.Get(app).ConfigureAwait(false);

		if (stats == null) {
			return candidates;
		}

		List<(AchRow Row, double Percent, bool Endgame)> ranked = [];

		foreach (AchRow row in candidates) {
			double percent = -1;
			bool endgame = GlobalStatsClient.LooksEndgame(row.Name);

			foreach (GlobalStatsClient.AchStat stat in stats) {
				if (stat.Name == row.Name) {
					percent = stat.Percent;
					endgame |= stat.Endgame;

					break;
				}
			}

			if (endgame && (progress < Config.Achievements.EndgameProgressThreshold)) {
				continue; // too early in the "playthrough" for finale material
			}

			// Insertion keeping order: non-endgame before endgame, then higher percent first.
			int at = ranked.Count;

			while (at > 0) {
				(_, double prevPercent, bool prevEndgame) = ranked[at - 1];

				if ((prevEndgame && !endgame) || ((prevEndgame == endgame) && (prevPercent < percent))) {
					at--;
				} else {
					break;
				}
			}

			ranked.Insert(at, (row, percent, endgame));
		}

		List<AchRow> result = [];

		foreach ((AchRow row, _, _) in ranked) {
			result.Add(row);
		}

		return result;
	}

	// A game whose achievements keep refusing to unlock has server-side stats (typical for MMOs);
	// probing the rest one by one wastes sessions, so write off achievements for that game entirely.
	private bool GiveUpIfProtected(uint app, GameEntry entry) {
		int giveUp = Config.Achievements.GiveUpAfterFailed;

		if ((giveUp <= 0) || (entry.AchFailed.Count < giveUp) || (entry.AchTarget == 0)) {
			return false;
		}

		entry.AchTarget = 0;
		Log($"{Library.Name(app)}: {entry.AchFailed.Count} achievements refused to unlock (server-side stats) — giving up on achievements in this game.");

		return true;
	}

	internal sealed record AchRow(int Index, bool Achieved, string Name);

	// Parses ASFAchievementManager "alist" output: lines like "12   [❌] Name" / "3   [✅] Name".
	internal static List<AchRow> ParseAchievementList(string text) {
		List<AchRow> rows = [];

		int pos = 0;

		while (pos < text.Length) {
			int end = text.IndexOf('\n', pos);

			if (end < 0) {
				end = text.Length;
			}

			int i = pos;

			while ((i < end) && char.IsWhiteSpace(text[i])) {
				i++;
			}

			int number = 0;
			bool hasNumber = false;

			while ((i < end) && (text[i] >= '0') && (text[i] <= '9')) {
				number = (number * 10) + (text[i] - '0');
				hasNumber = true;
				i++;
			}

			if (hasNumber) {
				bool isLocked = false;
				bool isAchieved = false;
				int mark = -1;

				for (int j = i; j < end; j++) {
					if (text[j] == '❌') {
						isLocked = true;
						mark = j;

						break;
					}

					if (text[j] == '✅') {
						isAchieved = true;
						mark = j;

						break;
					}
				}

				if (isLocked || isAchieved) {
					// The name follows the "] " after the status emoji.
					int nameStart = mark;

					while ((nameStart < end) && (text[nameStart] != ']')) {
						nameStart++;
					}

					string name = nameStart + 1 < end ? text.Substring(nameStart + 1, end - nameStart - 1).Trim() : "";
					rows.Add(new AchRow(number, isAchieved, name));
				}
			}

			pos = end + 1;
		}

		return rows;
	}
}
