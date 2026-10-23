using Xunit;

namespace UnderStudy.Tests;

public sealed class SteamReviewsClientTests {
	[Fact]
	public void Median_OddSampleCount_ReturnsMiddleValue() {
		List<int> samples = [50, 10, 30];

		Assert.Equal(30, SteamReviewsClient.Median(samples));
	}

	[Fact]
	public void Median_EvenSampleCount_ReturnsAverageOfMiddleTwo() {
		List<int> samples = [40, 10, 30, 20];

		Assert.Equal(25, SteamReviewsClient.Median(samples));
	}

	[Fact]
	public void Median_SingleSample_ReturnsThatSample() {
		List<int> samples = [77];

		Assert.Equal(77, SteamReviewsClient.Median(samples));
	}

	[Fact]
	public void Mean_ReturnsIntegerAverage() {
		List<int> samples = [10, 20, 33];

		// (10 + 20 + 33) / 3 = 21 (integer division, matches Mean's implementation).
		Assert.Equal(21, SteamReviewsClient.Mean(samples));
	}

	[Fact]
	public void ParsePlaytime_GetAppReviewsResponse_ReturnsMedian() {
		string reviews = string.Join(",", Enumerable.Range(1, 11).Select(i => "{\"author\":{\"playtime_forever\":" + (i * 10) + "}}"));
		string body = "{\"response\":{\"query_summary\":{\"num_reviews\":11},\"reviews\":[" + reviews + "]}}";

		Assert.Equal(60, SteamReviewsClient.ParsePlaytime(body, false));
	}

	[Fact]
	public void ParsePlaytime_TooFewSamples_ReturnsNull() {
		const string body = """{"response":{"reviews":[{"author":{"playtime_forever":30}}]}}""";

		Assert.Null(SteamReviewsClient.ParsePlaytime(body, false));
	}

	[Fact]
	public void ParsePlaytime_UnknownApp_ReturnsNull() {
		Assert.Null(SteamReviewsClient.ParsePlaytime("""{"response":{}}""", false));
	}
}
