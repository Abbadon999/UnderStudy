namespace UnderStudy;

/// <summary>
/// Fetches global achievement rarity from the Steam community stats page (display names, unlock
/// percentages, descriptions), used to unlock achievements in a believable order: common ones
/// first, "finish the game"-style ones last. The page is already sorted by percent descending.
/// </summary>
internal static class GlobalStatsClient {
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

	internal sealed record AchStat(string Name, double Percent, bool Endgame);

	// Phrases (checked against lowercase name + description) that mark an achievement as
	// end-of-game material; such achievements are deferred until most of the playtime cap is spent.
	private static readonly string[] EndgameMarkers = [
		"all episodes", "complete all", "complete the game", "beat the game", "finish the game",
		"finished the game", "all chapters", "all levels", "final episode", "final chapter", "100%"
	];

	private static readonly object CacheLock = new();
	private static readonly Dictionary<uint, (DateTime FetchedUtc, List<AchStat> Stats)> Cache = new();

	internal static async Task<List<AchStat>?> Get(uint appID) {
		lock (CacheLock) {
			if (Cache.TryGetValue(appID, out (DateTime FetchedUtc, List<AchStat> Stats) hit) && (DateTime.UtcNow - hit.FetchedUtc < CacheLifetime)) {
				return hit.Stats;
			}
		}

		string? html = await Net.GetString(new Uri($"https://steamcommunity.com/stats/{appID}/achievements/?l=english")).ConfigureAwait(false);

		if (string.IsNullOrEmpty(html)) {
			return null;
		}

		List<AchStat> stats = Parse(html);

		if (stats.Count == 0) {
			return null;
		}

		lock (CacheLock) {
			Cache[appID] = (DateTime.UtcNow, stats);
		}

		return stats;
	}

	internal static bool LooksEndgame(string text) {
		string lower = text.ToLowerInvariant();

		foreach (string marker in EndgameMarkers) {
			if (lower.Contains(marker, StringComparison.Ordinal)) {
				return true;
			}
		}

		return false;
	}

	internal static List<AchStat> Parse(string html) {
		List<AchStat> result = [];
		int pos = Find(html, "achieveRow", 0, html.Length);

		while (pos >= 0) {
			int next = Find(html, "achieveRow", pos + "achieveRow".Length, html.Length);
			int end = next < 0 ? html.Length : next;

			string name = Extract(html, pos, end, "<h3", "</h3>");
			string desc = Extract(html, pos, end, "<h5", "</h5>");
			string percentText = Extract(html, pos, end, "achievePercent", "<");

			if ((name.Length > 0) && (percentText.Length > 0)) {
				string digits = percentText.TrimEnd('%');

				if (double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent)) {
					result.Add(new AchStat(name, percent, LooksEndgame(name + " " + desc)));
				}
			}

			pos = next;
		}

		return result;
	}

	// Grabs the text between the first '>' following openMarker and closeMarker, within [from, to).
	private static string Extract(string s, int from, int to, string openMarker, string closeMarker) {
		int i = Find(s, openMarker, from, to);

		if (i < 0) {
			return "";
		}

		int gt = s.IndexOf('>', i);

		if ((gt < 0) || (gt >= to)) {
			return "";
		}

		int close = Find(s, closeMarker, gt + 1, to);

		if (close < 0) {
			return "";
		}

		return Decode(s.Substring(gt + 1, close - gt - 1).Trim());
	}

	// string.IndexOf(needle, from, StringComparison) has no upper bound of its own, so the match is
	// rejected if it falls outside [from, to) — same contract as a bounded search, backed by an
	// overload Reflect/ confirms ASF's own IL references (so it survives trimming).
	private static int Find(string haystack, string needle, int from, int to) {
		int index = haystack.IndexOf(needle, from, StringComparison.Ordinal);

		return (index >= 0) && (index <= to - needle.Length) ? index : -1;
	}

	private static string Decode(string s) => s.Contains('&', StringComparison.Ordinal)
		? s.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&lt;", "<").Replace("&gt;", ">")
		: s;
}
