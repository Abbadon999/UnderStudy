namespace UnderStudy;

/// <summary>Randomization helpers shared by the scheduler, selector and achievement planner.</summary>
internal static class Rng {
	internal static int Between(int min, int max) => max <= min ? min : Random.Shared.Next(min, max + 1);

	internal static void Shuffle<T>(IList<T> list) {
		for (int i = list.Count - 1; i > 0; i--) {
			int j = Random.Shared.Next(i + 1);
			(list[i], list[j]) = (list[j], list[i]);
		}
	}
}
