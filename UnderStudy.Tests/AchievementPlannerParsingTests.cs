using Xunit;

namespace UnderStudy.Tests;

public sealed class AchievementPlannerParsingTests {
	[Fact]
	public void ParseAchievementList_ReadsIndexStatusAndName() {
		const string alistOutput = """
			Achievements for TestBot:
			12   [❌] Locked One
			3   [✅] Unlocked Two

			Not a valid line
			""";

		List<AchievementPlanner.AchRow> rows = AchievementPlanner.ParseAchievementList(alistOutput);

		Assert.Equal(2, rows.Count);

		Assert.Equal(12, rows[0].Index);
		Assert.False(rows[0].Achieved);
		Assert.Equal("Locked One", rows[0].Name);

		Assert.Equal(3, rows[1].Index);
		Assert.True(rows[1].Achieved);
		Assert.Equal("Unlocked Two", rows[1].Name);
	}

	[Fact]
	public void ParseAchievementList_NoAchievementLines_ReturnsEmptyList() => Assert.Empty(AchievementPlanner.ParseAchievementList("No achievements found."));

	[Fact]
	public void ParseAchievementList_EmptyInput_ReturnsEmptyList() => Assert.Empty(AchievementPlanner.ParseAchievementList(""));
}
