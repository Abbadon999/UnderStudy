using Xunit;

namespace UnderStudy.Tests;

public sealed class UnderStudyConfigTests {
	[Fact]
	public void Validate_Defaults_ProduceNoWarnings() {
		UnderStudyConfig config = new();

		config.Validate(out List<string> warnings);

		Assert.Empty(warnings);
	}

	[Fact]
	public void Validate_MaxSessionBelowMin_WarnsAboutSessionRange() {
		UnderStudyConfig config = new() {
			MinSessionMinutes = 60,
			MaxSessionMinutes = 30
		};

		config.Validate(out List<string> warnings);

		Assert.Contains(warnings, w => w.Contains("Session minutes"));
	}

	[Fact]
	public void Validate_NonPositivePlaytimeFactor_WarnsAboutPlaytimeFactor() {
		UnderStudyConfig config = new() {
			PlaytimeFactor = 0
		};

		config.Validate(out List<string> warnings);

		Assert.Contains(warnings, w => w.Contains("PlaytimeFactor"));
	}

	[Theory]
	[InlineData(-0.1)]
	[InlineData(1.1)]
	public void Validate_FavoriteBiasOutOfRange_WarnsAboutFavoriteBias(double bias) {
		UnderStudyConfig config = new() {
			FavoriteBias = bias
		};

		config.Validate(out List<string> warnings);

		Assert.Contains(warnings, w => w.Contains("FavoriteBias"));
	}

	[Fact]
	public void Validate_MaxConcurrentGamesOutOfRange_WarnsAboutMaxConcurrentGames() {
		UnderStudyConfig config = new() {
			MaxConcurrentGames = 0
		};

		config.Validate(out List<string> warnings);

		Assert.Contains(warnings, w => w.Contains("MaxConcurrentGames"));
	}

	[Fact]
	public void UseAverage_DefaultsToFalse_UsesMedianSource() {
		UnderStudyConfig config = new();

		Assert.False(config.UseAverage);
	}

	[Fact]
	public void UseAverage_AverageForeverIsCaseInsensitive() {
		UnderStudyConfig config = new() {
			PlaytimeSource = "AVERAGE_FOREVER"
		};

		Assert.True(config.UseAverage);
	}
}
