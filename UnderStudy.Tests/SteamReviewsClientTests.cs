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
}
