namespace UnderStudy;

/// <summary>Per-bot configuration block, read from the "UnderStudy" key in <c>config/BotName.json</c>.</summary>
internal sealed class UnderStudyConfig {
	public bool Enabled { get; set; }

	// Session length (minutes) for a single "play" stretch of one game.
	public int MinSessionMinutes { get; set; } = 25;
	public int MaxSessionMinutes { get; set; } = 180;

	// Pause between sessions (minutes) where the bot is online but not in a game.
	public int MinGapMinutes { get; set; } = 3;
	public int MaxGapMinutes { get; set; } = 20;

	// Statistic over review authors' playtime: "median_forever" (default, robust) or "average_forever".
	public string PlaytimeSource { get; set; } = "median_forever";

	// Multiplier applied to the other-players playtime to derive the per-game lifetime cap.
	public double PlaytimeFactor { get; set; } = 1.0;

	// Cap used when there is not enough review data for a game.
	public int DefaultCapMinutes { get; set; } = 120;

	// How many games ASF plays at once (real accounts almost always run 1).
	public int MaxConcurrentGames { get; set; } = 1;

	// Probability that a session continues the current favorite game rather than picking something
	// else. 0 disables the favorite mechanic entirely.
	public double FavoriteBias { get; set; } = 0.7;

	// Probability per session start that the current favorite is dropped in favor of a new one.
	public double FavoriteSwitchChance { get; set; } = 0.08;

	// Probability that a session revisits a game that already reached its cap; the cap is not
	// extended, and achievements can still drop there.
	public double ReunionChance { get; set; } = 0.10;

	// Achievement pacing/unlocking — see AchievementsConfig; requires ASFAchievementManager.
	public AchievementsConfig Achievements { get; set; } = new();

	// If non-empty, only these appIDs are eligible.
	public HashSet<uint> IncludeAppIDs { get; set; } = [];
	public HashSet<uint> BlacklistAppIDs { get; set; } = [];

	// Stand down while ASF is actually farming trading cards.
	public bool RespectCardFarming { get; set; } = true;

	// What to do once every eligible game has reached its cap:
	// "Idle" (online, no game), "IdleSingle" (keep one game running),
	// "Stop" (stop rotating), "SecondPass" (raise factor and continue).
	public string FallbackMode { get; set; } = "Idle";

	// AppID used by IdleSingle fallback (0 = pick most-played owned game).
	public uint FallbackAppID { get; set; }

	// How often to refresh owned games + live playtime from Steam.
	public int RefreshOwnedMinutes { get; set; } = 30;

	// Optional High-realism knobs (null = disabled, the Balance default).
	// Active window in local server hours [start, end); end may exceed 24 to span midnight.
	public int? ActiveHoursStart { get; set; }
	public int? ActiveHoursEnd { get; set; }
	public double? MaxDailyHours { get; set; }

	// Optional Steam Web API key; only used as a fallback if access_token retrieval fails.
	public string? SteamApiKey { get; set; }

	internal bool UseAverage => string.Equals(PlaytimeSource, "average_forever", StringComparison.OrdinalIgnoreCase);

	internal void Validate(out List<string> warnings) {
		warnings = [];

		if ((MinSessionMinutes < 1) || (MaxSessionMinutes < MinSessionMinutes)) {
			warnings.Add($"Session minutes range looks off ({MinSessionMinutes}..{MaxSessionMinutes}).");
		}

		if ((MinGapMinutes < 0) || (MaxGapMinutes < MinGapMinutes)) {
			warnings.Add($"Gap minutes range looks off ({MinGapMinutes}..{MaxGapMinutes}).");
		}

		if (PlaytimeFactor <= 0) {
			warnings.Add($"PlaytimeFactor must be > 0 (got {PlaytimeFactor}).");
		}

		if (MaxConcurrentGames is < 1 or > 32) {
			warnings.Add($"MaxConcurrentGames must be 1..32 (got {MaxConcurrentGames}).");
		}

		if (FavoriteBias is < 0 or > 1) {
			warnings.Add($"FavoriteBias must be 0..1 (got {FavoriteBias}).");
		}

		if (FavoriteSwitchChance is < 0 or > 1) {
			warnings.Add($"FavoriteSwitchChance must be 0..1 (got {FavoriteSwitchChance}).");
		}

		if (ReunionChance is < 0 or > 1) {
			warnings.Add($"ReunionChance must be 0..1 (got {ReunionChance}).");
		}

		if ((Achievements.MinTargetPercent < 0) || (Achievements.MaxTargetPercent > 100) || (Achievements.MaxTargetPercent < Achievements.MinTargetPercent)) {
			warnings.Add($"Achievements target percent range looks off ({Achievements.MinTargetPercent}..{Achievements.MaxTargetPercent}).");
		}

		if (Achievements.EndgameProgressThreshold is < 0 or > 1) {
			warnings.Add($"Achievements.EndgameProgressThreshold must be 0..1 (got {Achievements.EndgameProgressThreshold}).");
		}

		if (Achievements.SkipFrontRunnerChance is < 0 or > 1) {
			warnings.Add($"Achievements.SkipFrontRunnerChance must be 0..1 (got {Achievements.SkipFrontRunnerChance}).");
		}
	}
}

/// <summary>Paced achievement unlocking; requires ASFAchievementManager to actually set achievements.</summary>
internal sealed class AchievementsConfig {
	public bool Enabled { get; set; }

	// Per-game completion target is rolled once from this range and never exceeded.
	public int MinTargetPercent { get; set; } = 25;
	public int MaxTargetPercent { get; set; } = 75;

	// Chance that a game gets no achievements at all.
	public double SkipGameChance { get; set; } = 0.15;

	// Upper bound on unlocks scheduled within one play session.
	public int MaxPerSession { get; set; } = 2;

	// After this many protected (server-side) unlock failures in one game, give up on that game's
	// achievements entirely instead of probing the rest one by one (MMOs often protect all of them).
	// 0 disables the give-up.
	public int GiveUpAfterFailed { get; set; } = 3;

	// "Finish the game"-style achievements are held back until this fraction of the playtime cap
	// is spent, so they can't drop in the first session.
	public double EndgameProgressThreshold { get; set; } = 0.85;

	// Chance the achievement pick skips the single most common candidate for the next one down —
	// avoids always taking the same "safest" pick when several are equally early-game.
	public double SkipFrontRunnerChance { get; set; } = 0.15;
}
