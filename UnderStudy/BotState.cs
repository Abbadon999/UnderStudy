using System.Text.Json.Serialization;
using ArchiSteamFarm.Helpers.Json;

namespace UnderStudy;

/// <summary>Persistent per-game bookkeeping.</summary>
internal sealed class GameEntry {
	public string Name { get; set; } = "";

	// Last known total playtime from Steam; also bumped optimistically after each session so the
	// cap is respected before Steam's own counter catches up.
	public int PlaytimeForever { get; set; }

	// Median/average playtime of other players (minutes), from Steam review authors; null = no data yet.
	// Field name kept as "SteamSpy*" for on-disk compatibility with state files from before the
	// SteamReviewsClient switch (SteamSpy's own median_forever/average_forever went to zero in 2018).
	public int? PlaytimeMetric { get; set; }

	public DateTime SteamSpyFetchedUtc { get; set; } = DateTime.MinValue;

	// Per-game randomization factor, stored as milliseconds (scaled int) rather than double: keeps
	// exact precision across a restart without depending on double round-tripping through JSON.
	[JsonPropertyName("JitterMilli")]
	public int JitterMilli { get; set; }

	[JsonIgnore]
	public double Jitter {
		get => JitterMilli / 1000.0;
		set => JitterMilli = (int) Math.Round(value * 1000.0);
	}

	// Final lifetime playtime target (minutes); null until computed.
	public int? CapMinutes { get; set; }

	// --- Achievements (via ASFAchievementManager) ---

	// Completion target in percent, rolled once; 0 = no achievements for this game; null = not rolled yet.
	public int? AchTarget { get; set; }

	// Achievement counts as last seen via "alist"; null total = never inspected.
	public int? AchTotal { get; set; }
	public int AchAchieved { get; set; }

	// Indexes that failed to unlock (server-protected) — never retried.
	public List<int> AchFailed { get; set; } = [];

	// Index attempted last; if still locked next time we check, it gets added to AchFailed.
	public int? AchLastTry { get; set; }
}

/// <summary>Scheduler state machine phases. See <see cref="BotScheduler"/> for the transitions.</summary>
internal enum SchedulerPhase {
	Idle,
	Playing,
	Gap,
	Fallback,
	FallbackSingle,
	Standby,
	Farming,
	OffHours,
	DailyCap,
	Stopped
}

/// <summary>Persistent scheduler state for a single bot, at <c>config/{BotName}_UnderStudy.db</c>.</summary>
internal sealed class BotState {
	public Dictionary<uint, GameEntry> Games { get; set; } = new();
	public List<uint> CurrentApps { get; set; } = [];
	public DateTime SessionStartUtc { get; set; } = DateTime.MinValue;
	public DateTime NextActionUtc { get; set; } = DateTime.MinValue;

	// Persisted as the enum member's own name (e.g. "Playing"), matched by hand rather than via
	// Enum.Parse/ToString — keeps this off any reflection-based path under ASF's trimmed runtime,
	// and keeps existing .db files on disk readable after this field became an enum in memory.
	[JsonPropertyName("Phase")]
	public string PhaseName { get; set; } = nameof(SchedulerPhase.Idle);

	[JsonIgnore]
	public SchedulerPhase Phase {
		get => PhaseName switch {
			nameof(SchedulerPhase.Playing) => SchedulerPhase.Playing,
			nameof(SchedulerPhase.Gap) => SchedulerPhase.Gap,
			nameof(SchedulerPhase.Fallback) => SchedulerPhase.Fallback,
			nameof(SchedulerPhase.FallbackSingle) => SchedulerPhase.FallbackSingle,
			nameof(SchedulerPhase.Standby) => SchedulerPhase.Standby,
			nameof(SchedulerPhase.Farming) => SchedulerPhase.Farming,
			nameof(SchedulerPhase.OffHours) => SchedulerPhase.OffHours,
			nameof(SchedulerPhase.DailyCap) => SchedulerPhase.DailyCap,
			nameof(SchedulerPhase.Stopped) => SchedulerPhase.Stopped,
			_ => SchedulerPhase.Idle
		};
		set => PhaseName = value switch {
			SchedulerPhase.Playing => nameof(SchedulerPhase.Playing),
			SchedulerPhase.Gap => nameof(SchedulerPhase.Gap),
			SchedulerPhase.Fallback => nameof(SchedulerPhase.Fallback),
			SchedulerPhase.FallbackSingle => nameof(SchedulerPhase.FallbackSingle),
			SchedulerPhase.Standby => nameof(SchedulerPhase.Standby),
			SchedulerPhase.Farming => nameof(SchedulerPhase.Farming),
			SchedulerPhase.OffHours => nameof(SchedulerPhase.OffHours),
			SchedulerPhase.DailyCap => nameof(SchedulerPhase.DailyCap),
			SchedulerPhase.Stopped => nameof(SchedulerPhase.Stopped),
			_ => nameof(SchedulerPhase.Idle)
		};
	}

	public DateTime LastOwnedRefreshUtc { get; set; } = DateTime.MinValue;
	public List<uint> Recent { get; set; } = [];

	// Session picks are biased toward this game (FavoriteBias) until FavoriteSwitchChance rolls a change.
	public uint FavoriteApp { get; set; }

	public bool PausedByCommand { get; set; }
	public string DailyDateUtc { get; set; } = "";

	// Persisted rounded to whole minutes, same scaled-value reasoning as GameEntry.Jitter.
	[JsonPropertyName("PlayedTodayMinutes")]
	public int PlayedTodayMinutesRounded { get; set; }

	[JsonIgnore]
	public double PlayedTodayMinutes {
		get => PlayedTodayMinutesRounded;
		set => PlayedTodayMinutesRounded = (int) Math.Round(value);
	}

	// Set once after the first Load()/Save(), surfaced in the log so a restart confirms persistence
	// is actually working. Instance field: a static one would leak one bot's message into every
	// other bot's log.
	internal string? LastDiagnostic;

	internal static BotState Load(string path) {
		try {
			if (!File.Exists(path)) {
				return new BotState();
			}

			// Async overload only: Reflect/ against a real ArchiSteamFarm.dll shows ASF itself calls
			// File.ReadAllTextAsync/WriteAllTextAsync but never the synchronous overloads, so only the
			// async ones are confirmed to survive trimming. Blocking here is fine — Load() only ever
			// runs once, from the constructor, before the timer starts.
			string json = File.ReadAllTextAsync(path).GetAwaiter().GetResult();

			return json.ToJsonObject<BotState>() ?? new BotState();
		} catch (Exception e) {
			return new BotState {
				LastDiagnostic = $"load failed, starting fresh: {e.GetType().Name}: {e.Message}"
			};
		}
	}

	internal void Save(string path) {
		try {
			// Same async-only constraint as Load(); Save() only ever runs from the scheduler's timer tick.
			File.WriteAllTextAsync(path, this.ToJsonText(true)).GetAwaiter().GetResult();
			LastDiagnostic ??= $"saved -> {path}";
		} catch (Exception e) {
			LastDiagnostic ??= $"save failed: {e.GetType().Name}: {e.Message}";
		}
	}
}
