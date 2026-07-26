using Xunit;

namespace UnderStudy.Tests;

public sealed class BotStateTests {
	private static string TempStatePath() => Path.Combine(Path.GetTempPath(), $"UnderStudyTest_{Guid.NewGuid():N}.db");

	[Fact]
	public void Load_MissingFile_ReturnsFreshIdleState() {
		BotState state = BotState.Load(TempStatePath());

		Assert.Equal(SchedulerPhase.Idle, state.Phase);
		Assert.Empty(state.Games);
	}

	// Exercises the real Save -> Load round trip through File.*AllTextAsync + JsonUtilities — the
	// exact path that used to call the synchronous, unconfirmed-safe File.ReadAllText.
	[Fact]
	public void SaveThenLoad_RoundTripsPhaseGamesAndJitter() {
		string path = TempStatePath();

		try {
			BotState original = new() {
				Phase = SchedulerPhase.FallbackSingle,
				FavoriteApp = 730,
				PlayedTodayMinutes = 42.0
			};

			original.Games[730] = new GameEntry {
				Name = "CS2",
				PlaytimeForever = 100,
				CapMinutes = 500,
				Jitter = 1.1
			};

			original.Save(path);

			BotState loaded = BotState.Load(path);

			Assert.Equal(SchedulerPhase.FallbackSingle, loaded.Phase);
			Assert.Equal(730u, loaded.FavoriteApp);
			Assert.Equal(42.0, loaded.PlayedTodayMinutes);
			Assert.Equal("CS2", loaded.Games[730].Name);
			Assert.Equal(500, loaded.Games[730].CapMinutes);
			Assert.Equal(1.1, loaded.Games[730].Jitter, precision: 3);
		} finally {
			File.Delete(path);
		}
	}

	// SchedulerPhase used to be a plain string; state files already on disk still spell phases this
	// way. This locks in that every enum member round-trips through the exact same text.
	[Theory]
	[InlineData("Playing")]
	[InlineData("Gap")]
	[InlineData("Fallback")]
	[InlineData("FallbackSingle")]
	[InlineData("Standby")]
	[InlineData("Farming")]
	[InlineData("OffHours")]
	[InlineData("DailyCap")]
	[InlineData("Stopped")]
	[InlineData("Idle")]
	public void Load_EveryLegacyPhaseString_RoundTripsToMatchingEnumMember(string phaseName) {
		string path = TempStatePath();

		try {
			File.WriteAllText(path, $$"""{"Phase":"{{phaseName}}"}""");

			BotState state = BotState.Load(path);

			Assert.Equal(phaseName, state.Phase.ToString());
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Load_UnknownPhaseString_DefaultsToIdle() {
		string path = TempStatePath();

		try {
			File.WriteAllText(path, """{"Phase":"SomeFutureVersionAddedThis"}""");

			BotState state = BotState.Load(path);

			Assert.Equal(SchedulerPhase.Idle, state.Phase);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Load_CorruptFile_ReturnsFreshStateWithDiagnostic() {
		string path = TempStatePath();

		try {
			File.WriteAllText(path, "{ not valid json");

			BotState state = BotState.Load(path);

			Assert.Empty(state.Games);
			Assert.NotNull(state.LastDiagnostic);
			Assert.Contains("load failed", state.LastDiagnostic);
		} finally {
			File.Delete(path);
		}
	}
}
