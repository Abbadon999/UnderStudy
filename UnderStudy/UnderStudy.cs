using System.Collections.Concurrent;
using System.Composition;
using System.Text.Json;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace UnderStudy;

[Export(typeof(IPlugin))]
internal sealed class UnderStudy : IPlugin, IBotModules, IBotConnection, IBotCommand2, IBot, IDisposable {
	private const string ConfigKey = "UnderStudy";

	private readonly ConcurrentDictionary<string, BotScheduler> Schedulers = new();

	public string Name => nameof(UnderStudy);

	public Version Version => typeof(UnderStudy).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);

	public Task OnLoaded() => Task.CompletedTask;

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		// Re-init: drop any previous scheduler for this bot.
		if (Schedulers.TryRemove(bot.BotName, out BotScheduler? previous)) {
			previous.Dispose();
		}

		UnderStudyConfig? config = TryLoadConfig(bot, additionalConfigProperties);

		if (config is not { Enabled: true }) {
			return Task.CompletedTask;
		}

		config.Validate(out List<string> warnings);

		foreach (string warning in warnings) {
			bot.ArchiLogger.LogGenericWarning($"[UnderStudy] {warning}");
		}

		Schedulers[bot.BotName] = new BotScheduler(bot, config);
		bot.ArchiLogger.LogGenericInfo("[UnderStudy] Enabled — will rotate through owned games.");

		return Task.CompletedTask;
	}

	private BotScheduler? FindScheduler(string botName) {
		if (Schedulers.TryGetValue(botName, out BotScheduler? exact)) {
			return exact;
		}

		foreach ((string name, BotScheduler scheduler) in Schedulers) {
			if (name.Equals(botName, StringComparison.OrdinalIgnoreCase)) {
				return scheduler;
			}
		}

		return null;
	}

	// ASF hands each plugin any config keys it doesn't recognize itself, already parsed. We deserialize
	// our "UnderStudy" key straight out of that JsonElement rather than re-reading the bot's config file.
	private static UnderStudyConfig? TryLoadConfig(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties) {
		if ((additionalConfigProperties == null) || !additionalConfigProperties.TryGetValue(ConfigKey, out JsonElement element)) {
			return null;
		}

		try {
			return element.ToJsonObject<UnderStudyConfig>();
		} catch (Exception e) {
			bot.ArchiLogger.LogGenericWarning($"[UnderStudy] Failed to read configuration: {e.Message}");

			return null;
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Schedulers.TryGetValue(bot.BotName, out BotScheduler? scheduler)) {
			scheduler.OnLoggedOn();
		}

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) => Task.CompletedTask;

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (Schedulers.TryRemove(bot.BotName, out BotScheduler? scheduler)) {
			scheduler.Dispose();
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);

		if ((args == null) || (args.Length == 0)) {
			return null;
		}

		if (!args[0].Equals("understudy", StringComparison.OrdinalIgnoreCase) && !args[0].Equals("us", StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		string sub = args.Length >= 2 ? args[1].ToLowerInvariant() : "status";

		// Optional explicit target bot: "understudy <sub> <BotName>". Lets a command addressed to one
		// bot control another, which matters because IPC routes bare commands to a single bot.
		string targetName = args.Length >= 3 ? args[2] : bot.BotName;

		BotScheduler? scheduler = FindScheduler(targetName);

		if (scheduler == null) {
			return bot.Commands.FormatBotResponse($"UnderStudy is not enabled for '{targetName}'.");
		}

		switch (sub) {
			case "status":
				if (access < EAccess.Operator) {
					return null;
				}

				return bot.Commands.FormatBotResponse(scheduler.StatusText());

			case "list":
				if (access < EAccess.Operator) {
					return null;
				}

				return bot.Commands.FormatBotResponse(scheduler.ListText());

			case "achnow":
				if (access < EAccess.Master) {
					return bot.Commands.FormatBotResponse("Access denied (Master required).");
				}

				return bot.Commands.FormatBotResponse(await scheduler.ForceUnlock().ConfigureAwait(false));

			case "pause":
				if (access < EAccess.Master) {
					return bot.Commands.FormatBotResponse("Access denied (Master required).");
				}

				await scheduler.Pause().ConfigureAwait(false);

				return bot.Commands.FormatBotResponse("UnderStudy rotation paused.");

			case "resume":
				if (access < EAccess.Master) {
					return bot.Commands.FormatBotResponse("Access denied (Master required).");
				}

				scheduler.Resume();

				return bot.Commands.FormatBotResponse("UnderStudy rotation resumed.");

			case "next":
				if (access < EAccess.Master) {
					return bot.Commands.FormatBotResponse("Access denied (Master required).");
				}

				await scheduler.Next().ConfigureAwait(false);

				return bot.Commands.FormatBotResponse(scheduler.StatusText());

			case "reset":
				if (access < EAccess.Master) {
					return bot.Commands.FormatBotResponse("Access denied (Master required).");
				}

				await scheduler.Reset().ConfigureAwait(false);

				return bot.Commands.FormatBotResponse("UnderStudy progress reset.");

			default:
				return bot.Commands.FormatBotResponse("Usage: understudy [status|list|pause|resume|next|reset]");
		}
	}

	public void Dispose() {
		foreach (BotScheduler scheduler in Schedulers.Values) {
			scheduler.Dispose();
		}

		Schedulers.Clear();
	}
}
