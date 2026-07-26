using System.Text;
using ArchiSteamFarm.Steam;

namespace UnderStudy;

/// <summary>
/// Drives one bot: rotates through owned games, playing each for a human-plausible amount of time
/// bounded by how long other players spend in it, with breaks between sessions. Game selection
/// lives in <see cref="GameSelector"/>, cap/library bookkeeping in <see cref="GameLibrary"/>, and
/// achievement pacing in <see cref="AchievementPlanner"/> — this class owns the tick loop, the
/// phase state machine, and the chat command surface.
/// </summary>
internal sealed class BotScheduler : IDisposable {
	private const int TickSeconds = 30;
	private const int CommandLockTimeoutMs = 10_000;
	private const double SecondPassCapMultiplier = 1.5;
	private const int MaxPlausibleSessionMinutes = 24 * 60;

	private readonly Bot Bot;
	private readonly UnderStudyConfig Config;
	private readonly string StatePath;
	private readonly BotState State;
	private readonly GameLibrary Library;
	private readonly GameSelector Selector;
	private readonly AchievementPlanner Achievements;
	private readonly SemaphoreSlim TickLock = new(1, 1);
	private readonly Timer Timer;

	private volatile bool Disposed;
	private bool DiagLogged;

	internal BotScheduler(Bot bot, UnderStudyConfig config) {
		Bot = bot;
		Config = config;

		string configPath = bot.GetFilePath(ArchiSteamFarm.Steam.Bot.EFileType.Config);
		string dir = Path.GetDirectoryName(configPath) ?? ".";
		StatePath = Path.Combine(dir, $"{bot.BotName}_UnderStudy.db"); // .db so ASF doesn't mistake this for a bot config.
		State = BotState.Load(StatePath);

		Library = new GameLibrary(bot, config, State, Log);
		Selector = new GameSelector(config, State, Library, Log);
		Achievements = new AchievementPlanner(bot, config, State, Library, Log);

		Timer = new Timer(_ => _ = Tick(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(TickSeconds));
	}

	public void Dispose() {
		Disposed = true;
		Timer.Dispose();
		TickLock.Dispose();
	}

	// Nudge an immediate evaluation, e.g. right after the bot logs back on.
	internal void OnLoggedOn() => _ = Tick();

	private void Log(string message) => Bot.ArchiLogger.LogGenericInfo($"[UnderStudy] {message}");

	private async Task Tick() {
		if (Disposed || !await TickLock.WaitAsync(0).ConfigureAwait(false)) {
			return;
		}

		try {
			await TickCore().ConfigureAwait(false);
			State.Save(StatePath);

			if (!DiagLogged && (State.LastDiagnostic is { } diag)) {
				DiagLogged = true;
				Log($"persistence: {diag}");
			}
		} catch (Exception e) {
			Bot.ArchiLogger.LogGenericException(e);
		} finally {
			TickLock.Release();
		}
	}

	private async Task TickCore() {
		if (State.PausedByCommand || !Bot.IsConnectedAndLoggedOn) {
			return;
		}

		// Steam only allows one session per account; when the user plays on their own PC,
		// IsPlayingPossible is false. Stand by and re-apply our session when control returns.
		if (!Bot.IsPlayingPossible) {
			SetPhase(SchedulerPhase.Standby, "playing not possible (account in use elsewhere) — standing by");

			return;
		}

		if (Config.RespectCardFarming && Bot.CardsFarmer.NowFarming) {
			SetPhase(SchedulerPhase.Farming, "ASF is farming cards — yielding until it finishes");

			return;
		}

		// Returning from an interruption with a live session still scheduled: re-assert it.
		if (State.Phase is SchedulerPhase.Standby or SchedulerPhase.Farming) {
			if ((State.CurrentApps.Count > 0) && (DateTime.UtcNow < State.NextActionUtc)) {
				await ApplyPlay(State.CurrentApps).ConfigureAwait(false);
				State.Phase = SchedulerPhase.Playing;
				Log($"Resumed session: {Describe(State.CurrentApps)} until {State.NextActionUtc:HH:mm} UTC.");

				return;
			}

			State.Phase = SchedulerPhase.Idle;
		}

		if (!WithinActiveHours()) {
			if (State.Phase != SchedulerPhase.OffHours) {
				await GoOnlineIdle().ConfigureAwait(false);
				SetPhase(SchedulerPhase.OffHours, "outside active hours — online, not playing");
			}

			return;
		}

		if (State.Phase == SchedulerPhase.OffHours) {
			State.Phase = SchedulerPhase.Idle;
		}

		RolloverDaily();

		if ((Config.MaxDailyHours is double maxDaily) && (State.PlayedTodayMinutes >= (maxDaily * 60.0))) {
			if (State.Phase != SchedulerPhase.DailyCap) {
				await GoOnlineIdle().ConfigureAwait(false);
				SetPhase(SchedulerPhase.DailyCap, $"daily limit of {maxDaily:0.#}h reached — idle until tomorrow");
			}

			return;
		}

		if (State.Phase == SchedulerPhase.DailyCap) {
			State.Phase = SchedulerPhase.Idle;
		}

		// A scheduled achievement moment inside the current session.
		if ((State.Phase == SchedulerPhase.Playing) && Achievements.TryTakeDueUnlock(State.CurrentApps, out uint dueApp)) {
			await Achievements.FireNext(dueApp).ConfigureAwait(false);
		}

		// Still mid-session or mid-gap.
		if (DateTime.UtcNow < State.NextActionUtc) {
			return;
		}

		if (State.Phase == SchedulerPhase.Playing) {
			AccumulateSession();
			await StartGap().ConfigureAwait(false);
		} else {
			await StartSessionOrFallback().ConfigureAwait(false);
		}
	}

	private void SetPhase(SchedulerPhase phase, string reason) {
		if (State.Phase != phase) {
			State.Phase = phase;
			Log(reason + ".");
		}
	}

	private async Task StartSessionOrFallback() {
		await Library.RefreshOwnedIfNeeded(force: State.Games.Count == 0).ConfigureAwait(false);

		List<uint> picks = await Selector.PickFavoriteOrGames(Config.MaxConcurrentGames).ConfigureAwait(false);

		if (picks.Count == 0) {
			await Fallback().ConfigureAwait(false);

			return;
		}

		// Clamp the session to the tightest positive cap; reunion picks are already past their cap
		// and play a full-length session without extending anything.
		int minRemaining = int.MaxValue;

		foreach (uint app in picks) {
			int remaining = Library.Remaining(app);

			if ((remaining > 0) && (remaining < minRemaining)) {
				minRemaining = remaining;
			}
		}

		int sessionLen = Math.Clamp(SessionMinutes(), 1, minRemaining);

		await ApplyPlay(picks).ConfigureAwait(false);

		State.CurrentApps = picks;
		State.SessionStartUtc = DateTime.UtcNow;
		State.NextActionUtc = DateTime.UtcNow.AddMinutes(sessionLen);
		State.Phase = SchedulerPhase.Playing;
		Selector.PushRecent(picks);
		Achievements.Plan(picks[0], sessionLen);

		string capNote = minRemaining != int.MaxValue ? $" (cap remaining ~{minRemaining} min)" : " (reunion)";
		Log($"Playing {Describe(picks)} for {sessionLen} min{capNote}.");
	}

	private async Task StartGap() {
		await GoOnlineIdle().ConfigureAwait(false);

		int gap = Rng.Between(Config.MinGapMinutes, Config.MaxGapMinutes);
		State.CurrentApps = [];
		State.NextActionUtc = DateTime.UtcNow.AddMinutes(gap);
		State.Phase = SchedulerPhase.Gap;

		Log($"Session ended — taking a {gap} min break.");
	}

	private async Task Fallback() {
		switch (Config.FallbackMode.ToLowerInvariant()) {
			case "idlesingle":
				uint app = Config.FallbackAppID != 0 ? Config.FallbackAppID : Library.MostPlayedOwned();

				if (app != 0) {
					await ApplyPlay([app]).ConfigureAwait(false);
					State.CurrentApps = [app];

					// Hourly rechecks re-assert the session silently; log only on entering the phase.
					if (State.Phase != SchedulerPhase.FallbackSingle) {
						State.Phase = SchedulerPhase.FallbackSingle;
						Log($"All games reached their caps — idling single game {Library.Name(app)} ({app}).");
					}

					State.NextActionUtc = DateTime.UtcNow.AddHours(1);

					return;
				}

				goto default;

			case "secondpass":
				foreach (GameEntry entry in State.Games.Values) {
					if (entry.CapMinutes is int cap) {
						entry.CapMinutes = (int) (cap * SecondPassCapMultiplier);
					}
				}

				State.Phase = SchedulerPhase.Idle;
				State.NextActionUtc = DateTime.UtcNow;
				Log($"All games reached their caps — starting another pass (caps x{SecondPassCapMultiplier}).");

				return;

			case "stop":
				await GoOnlineIdle().ConfigureAwait(false);
				State.CurrentApps = [];

				if (State.Phase != SchedulerPhase.Stopped) {
					State.Phase = SchedulerPhase.Stopped;
					Log("All games reached their caps — rotation stopped (will recheck hourly for new games).");
				}

				State.NextActionUtc = DateTime.UtcNow.AddHours(1);

				return;

			default: // "idle"
				await GoOnlineIdle().ConfigureAwait(false);
				State.CurrentApps = [];

				if (State.Phase != SchedulerPhase.Fallback) {
					State.Phase = SchedulerPhase.Fallback;
					Log("All games reached their caps — online idle (will recheck hourly for new games).");
				}

				State.NextActionUtc = DateTime.UtcNow.AddHours(1);

				return;
		}
	}

	private void AccumulateSession() {
		if (State.CurrentApps.Count == 0) {
			return;
		}

		double minutes = (DateTime.UtcNow - State.SessionStartUtc).TotalMinutes;

		if (minutes is <= 0 or > MaxPlausibleSessionMinutes) {
			return;
		}

		State.PlayedTodayMinutes += minutes;

		// Optimistically credit each game so its cap is honored before Steam's counter updates.
		foreach (uint app in State.CurrentApps) {
			if (State.Games.TryGetValue(app, out GameEntry? entry)) {
				entry.PlaytimeForever += (int) Math.Round(minutes);
			}
		}
	}

	// --- Steam interaction --------------------------------------------------------------------

	private async Task ApplyPlay(IReadOnlyCollection<uint> apps) {
		(bool success, string message) = await Bot.Actions.Play(apps).ConfigureAwait(false);

		if (!success) {
			Log($"Play({Describe(apps)}) failed: {message}");
		}
	}

	private async Task GoOnlineIdle() {
		// Hand control back to ASF. With GamesPlayedWhileIdle removed and nothing to farm,
		// the bot stays online but not in any game — our "break" state. A false result just means
		// nothing was paused ("Automatic farming is resumed already!") — benign, not worth logging.
		_ = Bot.Actions.Resume();

		await Task.CompletedTask.ConfigureAwait(false);
	}

	// --- Realism gates ------------------------------------------------------------------------

	private bool WithinActiveHours() {
		if ((Config.ActiveHoursStart is not int start) || (Config.ActiveHoursEnd is not int end)) {
			return true;
		}

		int hour = DateTime.Now.Hour;

		for (int i = start; i < end; i++) {
			if ((i % 24) == hour) {
				return true;
			}
		}

		return false;
	}

	private void RolloverDaily() {
		string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

		if (State.DailyDateUtc != today) {
			State.DailyDateUtc = today;
			State.PlayedTodayMinutes = 0;
		}
	}

	// --- Helpers ------------------------------------------------------------------------------

	// Session lengths skew short with occasional long ones: a quadratic warp of a uniform sample
	// gives that shape without Math.Log/Exp, which Reflect/ doesn't confirm as trimming-safe.
	private int SessionMinutes() {
		int min = Config.MinSessionMinutes;
		int max = Config.MaxSessionMinutes;

		if (max <= min) {
			return min;
		}

		double u = Random.Shared.NextDouble();

		return min + (int) Math.Round((max - min) * u * u);
	}

	private string Describe(IEnumerable<uint> apps) {
		StringBuilder sb = new();

		foreach (uint app in apps) {
			if (sb.Length > 0) {
				sb.Append(", ");
			}

			sb.Append(Library.Name(app)).Append(" (").Append(app).Append(')');
		}

		return sb.ToString();
	}

	// --- Command surface ----------------------------------------------------------------------

	internal string StatusText() {
		int total = State.Games.Count;
		int done = 0;

		foreach (GameEntry entry in State.Games.Values) {
			if ((entry.CapMinutes is int cap) && (entry.PlaytimeForever >= cap)) {
				done++;
			}
		}

		string current = State.CurrentApps.Count > 0 ? Describe(State.CurrentApps) : "—";
		string until = State.NextActionUtc > DateTime.UtcNow ? $"{(State.NextActionUtc - DateTime.UtcNow).TotalMinutes:0} min" : "now";
		string favorite = State.FavoriteApp != 0 ? $", favorite {Library.Name(State.FavoriteApp)}" : "";

		return $"UnderStudy: phase={State.Phase}{(State.PausedByCommand ? " (paused)" : "")}, now playing {current}{favorite}, next change in {until}, games {done}/{total} at cap.";
	}

	// Diagnostic: fire one achievement unlock for the game being played right now, bypassing the schedule.
	internal async Task<string> ForceUnlock() {
		if ((State.Phase != SchedulerPhase.Playing) || (State.CurrentApps.Count == 0)) {
			return "Not playing anything right now.";
		}

		uint app = State.CurrentApps[0];
		await Achievements.FireNext(app).ConfigureAwait(false);
		State.Save(StatePath);

		GameEntry? entry = State.Games.GetValueOrDefault(app);

		return $"{Library.Name(app)}: target={entry?.AchTarget?.ToString() ?? "?"}%, achieved={entry?.AchAchieved}/{entry?.AchTotal?.ToString() ?? "?"}, lastTry={entry?.AchLastTry?.ToString() ?? "-"}.";
	}

	internal string ListText() {
		List<(int Remaining, bool Known, uint App)> rows = [];
		int done = 0;
		long totalRemaining = 0;

		foreach ((uint app, GameEntry entry) in State.Games) {
			if (!Library.IsAllowed(app)) {
				continue;
			}

			int cap = entry.CapMinutes ?? Config.DefaultCapMinutes;
			int remaining = cap - entry.PlaytimeForever;

			if (remaining <= 0) {
				done++;

				continue;
			}

			totalRemaining += remaining;

			// Manual insertion keeps the list sorted by remaining minutes, descending (no LINQ/Sort;
			// Reflect/ found no List<T>.Sort reference in ASF's own IL, so it's not confirmed safe).
			int at = rows.Count;

			while ((at > 0) && (rows[at - 1].Remaining < remaining)) {
				at--;
			}

			rows.Insert(at, (remaining, entry.PlaytimeMetric != null, app));
		}

		StringBuilder sb = new();
		sb.Append($"Queue: {rows.Count} games, ~{totalRemaining / 60.0:0} h left to idle; {done} at cap.");

		int i = 0;

		foreach ((int remaining, bool known, uint app) in rows) {
			i++;
			string marker = State.CurrentApps.Contains(app) ? "▶ " : State.FavoriteApp == app ? "★ " : "";
			string estimate = known ? "" : " (estimate)";
			sb.Append('\n').Append($"{marker}{Library.Name(app)} [{i}/{rows.Count}] | {remaining / 60.0:0.0} h{estimate}");
		}

		return sb.ToString();
	}

	internal async Task Pause() {
		if (!await TickLock.WaitAsync(CommandLockTimeoutMs).ConfigureAwait(false)) {
			return;
		}

		try {
			State.PausedByCommand = true;
			await GoOnlineIdle().ConfigureAwait(false);
			State.Phase = SchedulerPhase.Idle;
			State.CurrentApps = [];
			Log("Rotation paused by command.");
			State.Save(StatePath);
		} finally {
			TickLock.Release();
		}
	}

	internal void Resume() {
		State.PausedByCommand = false;
		State.NextActionUtc = DateTime.UtcNow;
		Log("Rotation resumed by command.");
		State.Save(StatePath);
		_ = Tick();
	}

	internal async Task Next() {
		if (!await TickLock.WaitAsync(CommandLockTimeoutMs).ConfigureAwait(false)) {
			return;
		}

		try {
			if (!Bot.IsConnectedAndLoggedOn || !Bot.IsPlayingPossible) {
				return;
			}

			if (State.Phase == SchedulerPhase.Playing) {
				AccumulateSession();
			}

			State.PausedByCommand = false;
			await StartSessionOrFallback().ConfigureAwait(false);
			State.Save(StatePath);
		} finally {
			TickLock.Release();
		}
	}

	internal async Task Reset() {
		if (!await TickLock.WaitAsync(CommandLockTimeoutMs).ConfigureAwait(false)) {
			return;
		}

		try {
			State.Games.Clear();
			State.Recent.Clear();
			State.CurrentApps = [];
			State.FavoriteApp = 0;
			State.LastOwnedRefreshUtc = DateTime.MinValue;
			State.Phase = SchedulerPhase.Idle;
			State.NextActionUtc = DateTime.UtcNow;
			Log("Progress reset by command.");
			State.Save(StatePath);
		} finally {
			TickLock.Release();
		}
	}
}
