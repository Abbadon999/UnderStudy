using Xunit;

namespace UnderStudy.Tests;

public sealed class GlobalStatsClientTests {
	private const string SampleHtml = """
		<div class="achieveRow">
		  <div class="achieveTxt">
		    <h3>Test Achievement</h3>
		    <h5>Complete the game</h5>
		  </div>
		  <div class="achievePercent">82.3%</div>
		</div>
		<div class="achieveRow">
		  <div class="achieveTxt">
		    <h3>Another One</h3>
		    <h5>Finish &amp; survive</h5>
		  </div>
		  <div class="achievePercent">45.0%</div>
		</div>
		""";

	[Fact]
	public void Parse_ExtractsNamePercentAndEndgameFlagForEachRow() {
		List<GlobalStatsClient.AchStat> stats = GlobalStatsClient.Parse(SampleHtml);

		Assert.Equal(2, stats.Count);

		Assert.Equal("Test Achievement", stats[0].Name);
		Assert.Equal(82.3, stats[0].Percent, precision: 3);
		Assert.True(stats[0].Endgame, "Description contains an endgame marker phrase.");

		Assert.Equal("Another One", stats[1].Name);
		Assert.Equal(45.0, stats[1].Percent, precision: 3);
		Assert.False(stats[1].Endgame);
	}

	[Fact]
	public void Parse_DecodesHtmlEntitiesInExtractedText() {
		// The second row's description uses "&amp;"; LooksEndgame runs against the decoded text,
		// so this also guards against a regression that stops decoding before the endgame check.
		List<GlobalStatsClient.AchStat> stats = GlobalStatsClient.Parse(SampleHtml);

		Assert.False(stats[1].Endgame);
	}

	[Fact]
	public void Parse_EmptyHtml_ReturnsEmptyList() => Assert.Empty(GlobalStatsClient.Parse(""));

	[Theory]
	[InlineData("Complete the game", true)]
	[InlineData("Finish the game on hard", true)]
	[InlineData("All chapters cleared", true)]
	[InlineData("Kill your first enemy", false)]
	[InlineData("Collect 10 coins", false)]
	public void LooksEndgame_MatchesKnownMarkerPhrases(string text, bool expected) => Assert.Equal(expected, GlobalStatsClient.LooksEndgame(text));
}
