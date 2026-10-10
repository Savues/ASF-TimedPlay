using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace TimedPlay;

/// <summary>
///     Adds a <c>playfor</c> command that launches one or more games and, after a configured amount of time, stops
///     cards farming so the game is no longer "being played".
/// </summary>
/// <remarks>
///     Two things routinely kill a plain <c>play</c> session, and both are handled here:
///     <list type="bullet">
///         <item>
///             ASF clears the played games whenever <c>CheckOccupationStatus()</c> runs - which happens on every
///             Steam playing-session-state change and on every <c>reset</c>. A heartbeat re-asserts the games.
///         </item>
///         <item>
///             A Steam disconnect drops the played games entirely and ASF never restores them. The games are
///             re-asserted from <see cref="OnBotLoggedOn" /> as soon as the bot comes back.
///         </item>
///     </list>
///     One more trap, and the reason this file looks the way it does: ASF ships a trimmed, single-file build and
///     only preserves the BCL members it (or its own plugins) actually calls - see ASF's <c>TrimmerRoots.xml</c>,
///     which whitelists individual methods like <c>Enumerable.MaxBy</c> purely so third-party plugins keep working.
///     Any other BCL overload your plugin happens to use is simply gone, and it fails at runtime with a bare
///     <see cref="MissingMethodException" /> the first time that code path runs. Every BCL call below therefore
///     deliberately mirrors a call that already exists in ASF's own source.
/// </remarks>
[Export(typeof(IPlugin))]
internal sealed class TimedPlayPlugin : IPlugin, IBot, IBotCommand2, IBotConnection {
	private const string Command = "playfor";
	private const string SubCommandStatus = "status";
	private const string SubCommandCancel = "cancel";

	/// <summary>Hard limit imposed by the Steam network; ASF declares it internally, so it is repeated here.</summary>
	private const int MaxGamesPlayedConcurrently = 32;

	/// <summary>Timer due times are an unsigned int of milliseconds, which caps out at roughly 49.7 days.</summary>
	private const float MaxMinutes = 43000f;

	/// <summary>
	///     How often the games are re-asserted. Steam occasionally drops a playing session on its own, and ASF
	///     clears it without restoring anything, so a session left alone silently stops idling well before its deadline.
	/// </summary>
	private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

	private const string Usage = $"{Command} [Bots] <AppIDs> <Minutes> [GameName] | {Command} [Bots] {SubCommandStatus} | {Command} [Bots] {SubCommandCancel}";

	private readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

	[JsonInclude]
	public string Name => nameof(TimedPlayPlugin);

	[JsonInclude]
	public Version Version => typeof(TimedPlayPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() => Task.CompletedTask;

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDestroy(Bot bot) {
		Abort(bot);

		return Task.CompletedTask;
	}

	public Task OnBotLoggedOn(Bot bot) {
		// A reconnect wipes whatever ASF was playing, so restore it immediately instead of waiting for the heartbeat.
		if (TryGetActiveSession(bot, out Session? session)) {
			_ = Reassert(bot, session);
		}

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		if (TryGetActiveSession(bot, out Session? session)) {
			bot.ArchiLogger.LogGenericWarning($"{Command}: {session.Describe()} was interrupted by a Steam disconnect ({reason}); it will be restored on reconnect.");
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if ((args.Length < 2) || !args[0].Equals(Command, StringComparison.OrdinalIgnoreCase)) {
			return null;
		}

		// Optional [Bots] argument, resolved exactly like ASF core does (@all/@farming/@idle, ranges, regex).
		// Anything that parses as an AppID can never be a bot list, which keeps the two from being ambiguous.
		HashSet<Bot>? targets = TryParseAppId(args[1], out _) ? null : Bot.GetBots(args[1]);

		if (!((targets is { Count: > 0 }) && (args.Length > 2))) {
			targets = null;
		}

		int index = (targets != null) ? 2 : 1;
		Bot[] orderedTargets = (targets ?? new HashSet<Bot> { bot }).OrderBy(static target => target.BotName, StringComparer.OrdinalIgnoreCase).ToArray();

		if (index >= args.Length) {
			return FormatUsage(orderedTargets);
		}

		string subCommand = args[index];

		if (subCommand.Equals(SubCommandStatus, StringComparison.OrdinalIgnoreCase)) {
			if (access < EAccess.Operator) {
				return null;
			}

			return string.Join(Environment.NewLine, orderedTargets.Select(GetStatus));
		}

		if (subCommand.Equals(SubCommandCancel, StringComparison.OrdinalIgnoreCase)) {
			if (access < EAccess.Operator) {
				return null;
			}

			return string.Join(Environment.NewLine, orderedTargets.Select(Cancel));
		}

		if (access < EAccess.Master) {
			return null;
		}

		if ((index + 1) >= args.Length) {
			return FormatUsage(orderedTargets);
		}

		string[] gameTokens = args[index].Split(SharedInfo.ListElementSeparators, StringSplitOptions.RemoveEmptyEntries);

		if (gameTokens.Length == 0) {
			return FormatAll(orderedTargets, $"{Command}: no AppIDs given.");
		}

		HashSet<uint> gameIDs = [];

		foreach (string token in gameTokens) {
			if (!TryParseAppId(token, out uint appID)) {
				return FormatAll(orderedTargets, $"{Command}: invalid AppID `{token}`.");
			}

			if (!gameIDs.Add(appID)) {
				return FormatAll(orderedTargets, $"{Command}: duplicate AppID `{token}`.");
			}
		}

		if (gameIDs.Count > MaxGamesPlayedConcurrently) {
			return FormatAll(orderedTargets, $"{Command}: too many AppIDs, the limit is {MaxGamesPlayedConcurrently}.");
		}

		string minutesText = args[index + 1];

		// float.TryParse(string, NumberStyles, IFormatProvider) is the exact overload ASF itself calls, so it
		// survives trimming. See the remark on this class for why the obvious alternatives do not.
		if (!float.TryParse(minutesText, NumberStyles.Number, CultureInfo.InvariantCulture, out float minutes) || (minutes <= 0f) || (minutes > MaxMinutes)) {
			return FormatAll(orderedTargets, $"{Command}: invalid minutes `{minutesText}`, expected a number greater than 0 and at most {MaxMinutes:0}.");
		}

		// ASF's trimming removes System.Range.StartAt and string.Join(string, params string[]), so neither
		// array slicing with an explicit start nor the params overload may appear here.
		string? gameName = null;

		if ((index + 2) < args.Length) {
			List<string> nameParts = new List<string>();

			for (int offset = index + 2; offset < args.Length; offset++) {
				nameParts.Add(args[offset]);
			}

			gameName = string.Join(" ", nameParts);
		}

		string[] responses = await Task.WhenAll(orderedTargets.Select(target => Start(target, gameIDs, gameName, minutes))).ConfigureAwait(false);

		// string.Join(string, params string[]) is trimmed away; the IEnumerable<string> overload is not.
		return string.Join(Environment.NewLine, (IEnumerable<string>) responses);
	}

	private async Task<string> Start(Bot bot, HashSet<uint> gameIDs, string? gameName, float minutes) {
		string description = DescribeGames(gameIDs, gameName);

		if (!bot.IsConnectedAndLoggedOn) {
			return Format(bot, $"{Command}: bot is not connected / logged on.");
		}

		// Make sure a previous timer can never fire on top of this one.
		Abort(bot);

		try {
			(bool success, string message) = await bot.Actions.Play(gameIDs, gameName).ConfigureAwait(false);

			if (!success) {
				return Format(bot, $"{Command}: failed to start {description} - {message}");
			}
		}
		catch (Exception e) {
			bot.ArchiLogger.LogGenericException(e);

			return Format(bot, $"{Command}: failed to start {description} - {e.Message}");
		}

		TimeSpan duration = TimeSpan.FromMinutes(minutes);
		Session session = new(this, bot, [.. gameIDs], gameName, duration);

		Sessions[bot.BotName] = session;

		// Arm the timers only after the session is published, otherwise the callbacks could race the dictionary.
		session.Start();

		bot.ArchiLogger.LogGenericInfo($"{Command}: {description} started on {bot.BotName}, stopping in {FormatDuration(duration)} (heartbeat every {FormatDuration(HeartbeatInterval)}).");

		return Format(bot, $"started {description} - will be stopped in {FormatDuration(duration)}");
	}

	private string GetStatus(Bot bot) {
		if (!TryGetActiveSession(bot, out Session? session)) {
			return Format(bot, $"{Command}: no active timer.");
		}

		return Format(bot, $"{Command}: {session.Describe()} -> stopping in {FormatDuration(session.Remaining)} ({session.DueAt:yyyy-MM-dd HH:mm:ss} UTC), heartbeat in {FormatDuration(session.HeartbeatDueIn)}");
	}

	private string Cancel(Bot bot) {
		if (!Sessions.TryRemove(bot.BotName, out Session? session)) {
			return Format(bot, $"{Command}: no active timer to cancel.");
		}

		session.Cancel();

		return Format(bot, $"{Command}: timer for {session.Describe()} cancelled, the game keeps running.");
	}

	private void Abort(Bot bot) {
		if (Sessions.TryRemove(bot.BotName, out Session? session)) {
			session.Cancel();
		}
	}

	private void OnSessionDue(Bot bot, Session session) {
		if (!session.IsActive) {
			// Superseded by a newer session, so there is nothing left to stop.
			return;
		}

		Sessions.TryRemove(bot.BotName, out _);

		session.Cancel();

		_ = StopAsync(bot, session);
	}

	private void OnHeartbeat(Bot bot, Session session) {
		if (session.IsActive && bot.IsConnectedAndLoggedOn) {
			_ = Reassert(bot, session);
		}
	}

	private bool TryGetActiveSession(Bot bot, [NotNullWhen(true)] out Session? session) {
		if (Sessions.TryGetValue(bot.BotName, out session) && session.IsActive) {
			return true;
		}

		session = null;

		return false;
	}

	private static async Task Reassert(Bot bot, Session session) {
		try {
			// Re-asserting the games necessarily re-pauses the cards farmer, which is the point: a `playfor` session
			// owns the bot until its deadline, so a stray `resume` does not survive the next heartbeat. `cancel` is
			// the documented way to hand the bot back early.
			(bool success, string message) = await bot.Actions.Play(session.GameIDs, session.GameName).ConfigureAwait(false);

			if (!success) {
				bot.ArchiLogger.LogGenericWarning($"{Command}: heartbeat failed to re-assert {session.Describe()} on {bot.BotName} - {message}");
			}
		}
		catch (Exception e) {
			bot.ArchiLogger.LogGenericException(e);
		}
	}

	private static async Task StopAsync(Bot bot, Session session) {
		try {
			// Actions.Pause() never touches the game list: it short-circuits whenever the farmer is already
			// paused - which it always is here, because Actions.Play() pauses it when the session starts - and the
			// only branch that would clear the games is the non-permanent one. An empty Play() is what actually
			// stops the games; it reaches ArchiHandler.PlayGames([]), which is the same call ASF makes when it
			// clears a session by itself.
			(bool stopSuccess, string stopMessage) = await bot.Actions.Play([]).ConfigureAwait(false);

			if (!stopSuccess) {
				bot.ArchiLogger.LogGenericWarning($"{Command}: {session.Describe()} finished on {bot.BotName} but stopping the games failed - {stopMessage}");

				return;
			}

			// The farmer is already paused permanently by now, so "already paused" is the expected outcome
			// rather than a failure.
			(_, string pauseMessage) = await bot.Actions.Pause(permanent: true).ConfigureAwait(false);

			bot.ArchiLogger.LogGenericInfo($"{Command}: {session.Describe()} finished on {bot.BotName}, games stopped. {pauseMessage}");
		}
		catch (Exception e) {
			bot.ArchiLogger.LogGenericException(e);
		}
	}

	private static bool TryParseAppId(string text, out uint appID) {
		appID = 0;

		string trimmed = text.Trim();

		if (trimmed.Length == 0) {
			return false;
		}

		int slashIndex = trimmed.IndexOf('/');

		if (slashIndex >= 0) {
			string prefix = trimmed.Substring(0, slashIndex);

			if (!prefix.Equals("app", StringComparison.OrdinalIgnoreCase) && !prefix.Equals("a", StringComparison.OrdinalIgnoreCase)) {
				return false;
			}

			trimmed = trimmed.Substring(slashIndex + 1);
		}

		return uint.TryParse(trimmed, out appID) && (appID > 0);
	}

	private static string DescribeGames(IEnumerable<uint> gameIDs, string? gameName) {
		string ids = string.Join("+", gameIDs.Select(static gameID => gameID.ToString(CultureInfo.InvariantCulture)));

		return string.IsNullOrEmpty(gameName) ? ids : $"{ids} ({gameName})";
	}

	private static string FormatDuration(TimeSpan duration) {
		TimeSpan remaining = (duration < TimeSpan.Zero) ? TimeSpan.Zero : duration;

		return (remaining.TotalMinutes >= 1) ? $"{(int) remaining.TotalMinutes} minute(s)" : $"{(int) remaining.TotalSeconds} second(s)";
	}

	private static string Format(Bot bot, string message) => $"Bot {bot.BotName} | {message}";

	private static string FormatAll(IEnumerable<Bot> targets, string message) => string.Join(Environment.NewLine, targets.Select(target => Format(target, message)));

	private static string FormatUsage(IEnumerable<Bot> targets) => FormatAll(targets, $"usage: {Usage}");

	private sealed class Session {
		private readonly TimedPlayPlugin _plugin;
		private readonly Bot _bot;
		private readonly string? _gameName;
		private readonly TimeSpan _duration;

		private Timer? _stopTimer;
		private Timer? _heartbeatTimer;
		private DateTime _nextHeartbeat;

		internal Session(TimedPlayPlugin plugin, Bot bot, uint[] gameIDs, string? gameName, TimeSpan duration) {
			_plugin = plugin;
			_bot = bot;
			GameIDs = gameIDs;
			_gameName = gameName;
			_duration = duration;

			DueAt = DateTime.UtcNow + duration;
		}

		internal uint[] GameIDs { get; }

		internal string? GameName => _gameName;

		internal DateTime DueAt { get; }

		internal bool IsActive => _stopTimer != null;

		internal TimeSpan Remaining {
			get {
				TimeSpan remaining = DueAt - DateTime.UtcNow;

				return (remaining < TimeSpan.Zero) ? TimeSpan.Zero : remaining;
			}
		}

		internal TimeSpan HeartbeatDueIn {
			get {
				TimeSpan remaining = _nextHeartbeat - DateTime.UtcNow;

				return (remaining < TimeSpan.Zero) ? TimeSpan.Zero : remaining;
			}
		}

		internal string Describe() => DescribeGames(GameIDs, _gameName);

		internal void Start() {
			_nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;

			_stopTimer = new Timer(_ => _plugin.OnSessionDue(_bot, this), null, _duration, Timeout.InfiniteTimeSpan);

			_heartbeatTimer = new Timer(
				_ => {
					_nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;

					_plugin.OnHeartbeat(_bot, this);
				},
				null,
				HeartbeatInterval,
				HeartbeatInterval
			);
		}

		internal void Cancel() {
			// Timer.Dispose() is idempotent, so a plain swap is enough here and avoids Interlocked, which ASF
			// never calls and which therefore does not survive its trimming.
			Timer? stopTimer = _stopTimer;
			Timer? heartbeatTimer = _heartbeatTimer;

			_stopTimer = null;
			_heartbeatTimer = null;

			stopTimer?.Dispose();
			heartbeatTimer?.Dispose();
		}
	}
}
