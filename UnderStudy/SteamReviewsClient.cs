using ArchiSteamFarm.Helpers.Json;

namespace UnderStudy;

/// <summary>Estimates how long other people play a game from the playtime of its 100 most recent reviewers.</summary>
internal static class SteamReviewsClient {
	// Below this many usable samples the estimate is noise; callers fall back to DefaultCapMinutes.
	private const int MinSamples = 10;

	// Minimum spacing between requests to the store endpoint.
	private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.2);

	private static readonly SemaphoreSlim RateLimiter = new(1, 1);
	private static DateTime LastRequestUtc = DateTime.MinValue;

	/// <summary>Estimated playtime metric (minutes) for the app, or null if there is not enough review data.</summary>
	internal static async Task<int?> GetPlaytimeMinutes(uint appID, bool useAverage) {
		Uri request = new($"https://api.steampowered.com/IUserReviewsService/GetAppReviews/v1/?appid={appID}&num_per_page=100&filter=1&purchase_type=all&language=all");

		await RateLimiter.WaitAsync().ConfigureAwait(false);

		string? body;

		try {
			TimeSpan sinceLast = DateTime.UtcNow - LastRequestUtc;

			if (sinceLast < MinRequestInterval) {
				await Task.Delay(MinRequestInterval - sinceLast).ConfigureAwait(false);
			}

			body = await Net.GetString(request).ConfigureAwait(false);
			LastRequestUtc = DateTime.UtcNow;
		} finally {
			RateLimiter.Release();
		}

		return string.IsNullOrEmpty(body) ? null : ParsePlaytime(body, useAverage);
	}

	internal static int? ParsePlaytime(string body, bool useAverage) {
		try {
			List<ReviewEntry>? reviews = body.ToJsonObject<AppReviewsResponse>()?.Response?.Reviews;

			if (reviews == null) {
				return null;
			}

			List<int> samples = [];

			foreach (ReviewEntry review in reviews) {
				int playtime = review.Author?.PlaytimeForever ?? 0;

				if (playtime > 0) {
					samples.Add(playtime);
				}
			}

			if (samples.Count < MinSamples) {
				return null;
			}

			return useAverage ? Mean(samples) : Median(samples);
		} catch {
			return null;
		}
	}

	internal static int Mean(List<int> values) {
		long sum = 0;

		foreach (int v in values) {
			sum += v;
		}

		return (int) (sum / values.Count);
	}

	// Manual insertion sort: Reflect/ found no List<T>.Sort reference anywhere in ASF's own IL, so
	// unlike the string/File overloads used elsewhere in this plugin, this one has no positive
	// evidence either way. n stays small (≤100 review samples), so the O(n²) cost doesn't matter.
	internal static int Median(List<int> values) {
		for (int i = 1; i < values.Count; i++) {
			int key = values[i];
			int j = i - 1;

			while ((j >= 0) && (values[j] > key)) {
				values[j + 1] = values[j];
				j--;
			}

			values[j + 1] = key;
		}

		int mid = values.Count / 2;

		return (values.Count % 2) != 0 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
	}
}
