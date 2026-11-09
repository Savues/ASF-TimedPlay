using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Composition;
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
///     Adds a <c>playfor</c> command - also reachable as the one-letter <c>f</c> - that launches one or more games
///     and, after a configured amount of time, stops them so cards farming can resume.
///     Supports concurrent execution of multiple games with independent timers.
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
	private const string CommandShort = "f";
	private const string SubCommandStatus = "status";
	private const string SubCommandCancel = "cancel";
	private const string SubCommandStop = "stop";

	/// <summary>Hard limit imposed by the Steam network; ASF declares it internally, so it is repeated here.</summary>
	private const int MaxGamesPlayedConcurrently = 32;

	/// <summary>Timer due times are an unsigned int of milliseconds, which caps out at roughly 49.7 days.</summary>
	private const float MaxMinutes = 43000f;

	/// <summary>Duration applied when the command is given without one.</summary>
	private const float DefaultMinutes = 35f;

	/// <summary>Interval between automatic std executions. The server-side rate limit is 5 minutes.</summary>
	private static readonly TimeSpan AutoStdInterval = TimeSpan.FromMinutes(6);

	/// <summary>
	///     How often the games are re-asserted. Steam occasionally drops a playing session on its own, and ASF
	///     clears it without restoring anything, so a session left alone silently stops idling well before its deadline.
	/// </summary>
	private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

	private const string Usage = $"{Command}|{CommandShort} [Bots] <AppIDs> [Minutes] | {Command}|{CommandShort} [Bots] {SubCommandStatus} | {Command}|{CommandShort} [Bots] {SubCommandCancel} [AppIDs] | {Command}|{CommandShort} [Bots] {SubCommandStop} [AppIDs]";

	private readonly ConcurrentDictionary<string, BotSession> Sessions = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Timer for periodic std command execution.</summary>
	private Timer? _stdDumpTimer;

	[JsonInclude]
	public string Name => nameof(TimedPlayPlugin);

	[JsonInclude]
	public Version Version => typeof(TimedPlayPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() {
		_stdDumpTimer = new Timer(ExecuteStdDump, null, AutoStdInterval, AutoStdInterval);

		return Task.CompletedTask;
	}

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDestroy(Bot bot) {
		if (Sessions.TryRemove(bot.BotName, out BotSession? session)) {
			session.Dispose();
		}

		return Task.CompletedTask;
	}

	private async void ExecuteStdDump(object? state = null) {
		HashSet<Bot>? bots = Bot.GetBots("@all");

		if ((bots == null) || (bots.Count == 0)) {
			ArchiSteamFarm.Core.ASF.ArchiLogger.LogGenericWarning("[TimedPlay] auto std skipped: no bots resolved");

			return;
		}

		foreach (Bot bot in bots.Where(static bot => bot.IsConnectedAndLoggedOn)) {
			try {
				string? response = await bot.Commands.Response(EAccess.Owner, "std").ConfigureAwait(false);

				bot.ArchiLogger.LogGenericInfo($"[TimedPlay] auto std for {bot.BotName}: {response ?? "<no response>"}");
			} catch (Exception e) {
				bot.ArchiLogger.LogGenericException(e);
			}
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		// A reconnect wipes whatever ASF was playing, so restore active games immediately instead of waiting for heartbeat.
		if (Sessions.TryGetValue(bot.BotName, out BotSession? session) && session.HasActiveTasks) {
			_ = session.ReassertAsync();
		}

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		if (Sessions.TryGetValue(bot.BotName, out BotSession? session) && session.HasActiveTasks) {
			bot.ArchiLogger.LogGenericWarning($"{Command}: active games on {bot.BotName} were interrupted by a Steam disconnect ({reason}); they will be restored on reconnect.");
		}

		return Task.CompletedTask;
	}

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		if ((args.Length < 1) || !IsCommand(args[0])) {
			return null;
		}

		if (access < EAccess.Operator) {
			return null;
		}

		if (args.Length == 1) {
			return FormatUsage([bot]);
		}

		// Optional [Bots] argument, resolved exactly like ASF core does (@all/@farming/@idle, ranges, regex).
		// Disambiguation order:
		// 1. If args[1] is a known subcommand or a valid AppID list, the target is implicitly the issuing bot.
		// 2. Otherwise, treat args[1] as a bot list.
		HashSet<Bot>? targets = (IsSubCommand(args[1]) || IsAppIdList(args[1])) ? null : Bot.GetBots(args[1]);

		if (targets != null && targets.Count == 0) {
			return Format(bot, $"{Command}: no bots matched `{args[1]}`.");
		}

		int index = (targets != null) ? 2 : 1;
		Bot[] orderedTargets = (targets ?? [bot]).OrderBy(static target => target.BotName, StringComparer.OrdinalIgnoreCase).ToArray();

		if (index >= args.Length) {
			return FormatUsage(orderedTargets);
		}

		string subCommand = args[index];

		if (subCommand.Equals(SubCommandStatus, StringComparison.OrdinalIgnoreCase)) {
			if ((index + 1) < args.Length) {
				return FormatUsage(orderedTargets);
			}

			return string.Join(Environment.NewLine, (IEnumerable<string>) orderedTargets.Select(GetStatus));
		}

		if (subCommand.Equals(SubCommandCancel, StringComparison.OrdinalIgnoreCase)) {
			if (access < EAccess.Master) {
				return null;
			}

			HashSet<uint>? cancelIDs = null;

			if ((index + 1) < args.Length) {
				if ((index + 2) < args.Length) {
					return FormatUsage(orderedTargets);
				}

				string[] tokens = args[index + 1].Split(SharedInfo.ListElementSeparators, StringSplitOptions.RemoveEmptyEntries);

				if (tokens.Length == 0) {
					return FormatAll(orderedTargets, $"{Command}: no AppIDs given to cancel.");
				}

				cancelIDs = [];

				foreach (string token in tokens) {
					if (!TryParseAppId(token, out uint appID)) {
						return FormatAll(orderedTargets, $"{Command}: invalid AppID `{token}`.");
					}

					cancelIDs.Add(appID);
				}
			}

			string[] cancelResponses = await Task.WhenAll(orderedTargets.Select(target => Cancel(target, cancelIDs))).ConfigureAwait(false);

			return string.Join(Environment.NewLine, (IEnumerable<string>) cancelResponses);
		}

		if (subCommand.Equals(SubCommandStop, StringComparison.OrdinalIgnoreCase)) {
			if (access < EAccess.Master) {
				return null;
			}

			HashSet<uint>? stopIDs = null;

			if ((index + 1) < args.Length) {
				if ((index + 2) < args.Length) {
					return FormatUsage(orderedTargets);
				}

				string[] tokens = args[index + 1].Split(SharedInfo.ListElementSeparators, StringSplitOptions.RemoveEmptyEntries);

				if (tokens.Length == 0) {
					return FormatAll(orderedTargets, $"{Command}: no AppIDs given to stop.");
				}

				stopIDs = [];

				foreach (string token in tokens) {
					if (!TryParseAppId(token, out uint appID)) {
						return FormatAll(orderedTargets, $"{Command}: invalid AppID `{token}`.");
					}

					stopIDs.Add(appID);
				}
			}

			string[] stopResponses = await Task.WhenAll(orderedTargets.Select(target => Stop(target, stopIDs))).ConfigureAwait(false);

			return string.Join(Environment.NewLine, (IEnumerable<string>) stopResponses);
		}

		if (access < EAccess.Master) {
			return null;
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

			gameIDs.Add(appID);
		}

		if (gameIDs.Count > MaxGamesPlayedConcurrently) {
			return FormatAll(orderedTargets, $"{Command}: too many AppIDs, the limit is {MaxGamesPlayedConcurrently}.");
		}

		float minutes = DefaultMinutes;

		if ((index + 1) < args.Length) {
			if (!float.TryParse(args[index + 1], NumberStyles.Number, CultureInfo.InvariantCulture, out float parsedMinutes)) {
				return FormatAll(orderedTargets, $"{Command}: invalid minutes `{args[index + 1]}`, expected a positive number. Note: custom game names are not supported. Usage: {Usage}");
			}

			if ((parsedMinutes <= 0f) || (parsedMinutes > MaxMinutes)) {
				return FormatAll(orderedTargets, $"{Command}: invalid minutes `{args[index + 1]}`, expected a number greater than 0 and at most {MaxMinutes:0}.");
			}

			minutes = parsedMinutes;
		}

		if ((index + 2) < args.Length) {
			return FormatAll(orderedTargets, $"{Command}: unexpected extra argument `{args[index + 2]}`. Custom game names are not supported. Usage: {Usage}");
		}

		string[] startResponses = await Task.WhenAll(orderedTargets.Select(target => Start(target, gameIDs, minutes))).ConfigureAwait(false);

		return string.Join(Environment.NewLine, (IEnumerable<string>) startResponses);
	}

	private static bool IsCommand(string value) => value.Equals(Command, StringComparison.OrdinalIgnoreCase) || value.Equals(CommandShort, StringComparison.OrdinalIgnoreCase);

	private static bool IsSubCommand(string value) => value.Equals(SubCommandStatus, StringComparison.OrdinalIgnoreCase) || value.Equals(SubCommandCancel, StringComparison.OrdinalIgnoreCase) || value.Equals(SubCommandStop, StringComparison.OrdinalIgnoreCase);

	private async Task<string> Start(Bot bot, HashSet<uint> gameIDs, float minutes) {
		BotSession session = Sessions.GetOrAdd(bot.BotName, _ => new BotSession(this, bot));

		return await session.AddOrUpdateGamesAsync(gameIDs, TimeSpan.FromMinutes(minutes)).ConfigureAwait(false);
	}

	private string GetStatus(Bot bot) {
		if (!Sessions.TryGetValue(bot.BotName, out BotSession? session) || !session.HasActiveTasks) {
			return Format(bot, $"{Command}: no active timers.");
		}

		return session.GetStatus();
	}

	private async Task<string> Cancel(Bot bot, HashSet<uint>? appIDs) {
		if (!Sessions.TryGetValue(bot.BotName, out BotSession? session) || !session.HasActiveTasks) {
			return Format(bot, $"{Command}: no active timers to cancel.");
		}

		return await session.CancelAsync(appIDs).ConfigureAwait(false);
	}

	private async Task<string> Stop(Bot bot, HashSet<uint>? appIDs) {
		if (!Sessions.TryGetValue(bot.BotName, out BotSession? session) || !session.HasActiveTasks) {
			return Format(bot, $"{Command}: no active games to stop.");
		}

		return await session.StopAsync(appIDs).ConfigureAwait(false);
	}

	private void OnGameDue(Bot bot, uint appID) {
		if (Sessions.TryGetValue(bot.BotName, out BotSession? session)) {
			_ = session.HandleGameDueAsync(appID);
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

	private static bool IsAppIdList(string text) {
		string[] tokens = text.Split(SharedInfo.ListElementSeparators, StringSplitOptions.RemoveEmptyEntries);

		if (tokens.Length == 0) {
			return false;
		}

		foreach (string token in tokens) {
			if (!TryParseAppId(token, out _)) {
				return false;
			}
		}

		return true;
	}

	private static string DescribeGames(IEnumerable<uint> gameIDs) {
		List<string> list = gameIDs.Select(static gameID => gameID.ToString(CultureInfo.InvariantCulture)).ToList();

		return (list.Count > 0) ? string.Join("+", (IEnumerable<string>) list) : "<none>";
	}

	private static string FormatDuration(TimeSpan duration) {
		TimeSpan remaining = (duration < TimeSpan.Zero) ? TimeSpan.Zero : duration;

		return (remaining.TotalMinutes >= 1) ? $"{(int) remaining.TotalMinutes} minute(s)" : $"{(int) remaining.TotalSeconds} second(s)";
	}

	private static string Format(Bot bot, string message) => $"Bot {bot.BotName} | {message}";

	private static string FormatAll(IEnumerable<Bot> targets, string message) => string.Join(Environment.NewLine, (IEnumerable<string>) targets.Select(target => Format(target, message)));

	private static string FormatUsage(IEnumerable<Bot> targets) => FormatAll(targets, $"usage: {Usage}");

	private sealed class GameTask : IDisposable {
		internal uint AppID { get; }
		internal DateTime DueAt { get; }
		private Timer? _timer;

		internal TimeSpan Remaining {
			get {
				TimeSpan remaining = DueAt - DateTime.UtcNow;

				return (remaining < TimeSpan.Zero) ? TimeSpan.Zero : remaining;
			}
		}

		internal GameTask(uint appID, DateTime dueAt, TimerCallback callback, TimeSpan duration) {
			AppID = appID;
			DueAt = dueAt;
			_timer = new Timer(callback, null, duration, Timeout.InfiniteTimeSpan);
		}

		public void Dispose() {
			Timer? timer = _timer;
			_timer = null;
			timer?.Dispose();
		}
	}

	private sealed class BotSession : IDisposable {
		private readonly TimedPlayPlugin _plugin;
		private readonly Bot _bot;
		private readonly SemaphoreSlim _lock = new(1, 1);
		private readonly ConcurrentDictionary<uint, GameTask> _tasks = new();
		private readonly ConcurrentDictionary<uint, byte> _detachedGames = new();
		private Timer? _heartbeatTimer;
		private DateTime _nextHeartbeat;
		private bool _initialCardsPaused;

		internal BotSession(TimedPlayPlugin plugin, Bot bot) {
			_plugin = plugin;
			_bot = bot;
		}

		private HashSet<uint> GetAllActiveGameIDs() {
			HashSet<uint> all = new(_tasks.Keys);

			foreach (uint id in _detachedGames.Keys) {
				all.Add(id);
			}

			return all;
		}

		internal bool HasActiveTasks => !_tasks.IsEmpty || !_detachedGames.IsEmpty;

		internal async Task<string> AddOrUpdateGamesAsync(HashSet<uint> gameIDs, TimeSpan duration) {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				if (!_bot.IsConnectedAndLoggedOn) {
					return Format(_bot, $"{Command}: bot is not connected / logged on.");
				}

				HashSet<uint> plannedKeys = GetAllActiveGameIDs();

				foreach (uint id in gameIDs) {
					plannedKeys.Add(id);
				}

				if (plannedKeys.Count > MaxGamesPlayedConcurrently) {
					return Format(_bot, $"{Command}: adding {gameIDs.Count} game(s) would result in {plannedKeys.Count} concurrent games, exceeding the limit of {MaxGamesPlayedConcurrently}.");
				}

				if (_tasks.IsEmpty && _detachedGames.IsEmpty) {
					_initialCardsPaused = _bot.CardsFarmer.Paused;
				}

				uint[] playArray = [.. plannedKeys];
				(bool success, string message) = await _bot.Actions.Play(playArray).ConfigureAwait(false);

				if (!success) {
					if (_tasks.IsEmpty && _detachedGames.IsEmpty) {
						return Format(_bot, $"{Command}: failed to play {DescribeGames(gameIDs)} - {message}");
					}

					_ = await _bot.Actions.Play([.. GetAllActiveGameIDs()]).ConfigureAwait(false);

					return Format(_bot, $"{Command}: failed to play {DescribeGames(gameIDs)} - {message}");
				}

				DateTime dueAt = DateTime.UtcNow + duration;

				foreach (uint id in gameIDs) {
					_detachedGames.TryRemove(id, out _);

					if (_tasks.TryRemove(id, out GameTask? existing)) {
						existing.Dispose();
					}

					uint currentId = id;
					GameTask newTask = new(currentId, dueAt, _ => _plugin.OnGameDue(_bot, currentId), duration);
					_tasks[currentId] = newTask;
				}

				EnsureHeartbeatRunning();

				_bot.ArchiLogger.LogGenericInfo($"{Command}: {DescribeGames(gameIDs)} started/updated on {_bot.BotName}, running for {FormatDuration(duration)} (total active: {plannedKeys.Count}).");

				return Format(_bot, $"started/updated {DescribeGames(gameIDs)} - will run for {FormatDuration(duration)} (active: {plannedKeys.Count}/{MaxGamesPlayedConcurrently})");
			}
			catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);

				return Format(_bot, $"{Command}: failed to start {DescribeGames(gameIDs)} - {e.Message}");
			}
			finally {
				_lock.Release();
			}
		}

		internal async Task HandleGameDueAsync(uint appID) {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				if (!_tasks.TryRemove(appID, out GameTask? task)) {
					return;
				}

				task.Dispose();

				_bot.ArchiLogger.LogGenericInfo($"{Command}: game {appID} timer expired on {_bot.BotName}.");

				if (!_tasks.IsEmpty) {
					uint[] remainingIds = [.. GetAllActiveGameIDs()];
					(bool success, string message) = await _bot.Actions.Play(remainingIds).ConfigureAwait(false);

					if (!success) {
						_bot.ArchiLogger.LogGenericWarning($"{Command}: failed to reconcile games on {_bot.BotName} after {appID} ended - {message}");
					}
				} else {
					StopHeartbeat();

					if (!_detachedGames.IsEmpty) {
						uint[] detachedIds = [.. _detachedGames.Keys];
						(bool success, string message) = await _bot.Actions.Play(detachedIds).ConfigureAwait(false);

						if (!success) {
							_bot.ArchiLogger.LogGenericWarning($"{Command}: failed to reconcile detached games on {_bot.BotName} after timed games ended - {message}");
						}

						_bot.ArchiLogger.LogGenericInfo($"{Command}: all timed games finished on {_bot.BotName}; detached games ({DescribeGames(detachedIds)}) keep running on Steam.");
					} else {
						await StopAllGamesAndRestoreFarmingAsync().ConfigureAwait(false);
					}
				}
			}
			catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);
			}
			finally {
				_lock.Release();
			}
		}

		internal async Task HeartbeatAsync() {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				if (_tasks.IsEmpty) {
					StopHeartbeat();

					return;
				}

				_nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;

				if (_bot.IsConnectedAndLoggedOn) {
					uint[] activeIds = [.. GetAllActiveGameIDs()];
					_bot.ArchiLogger.LogGenericInfo($"{Command}: heartbeat - {_tasks.Count} timed + {_detachedGames.Count} detached game(s) active on {_bot.BotName} ({DescribeGames(activeIds)}).");

					(bool success, string message) = await _bot.Actions.Play(activeIds).ConfigureAwait(false);

					if (!success) {
						_bot.ArchiLogger.LogGenericWarning($"{Command}: heartbeat failed to re-assert games on {_bot.BotName} - {message}");
					}
				} else {
					_bot.ArchiLogger.LogGenericWarning($"{Command}: heartbeat - {GetAllActiveGameIDs().Count} game(s) active on {_bot.BotName} but bot is offline.");
				}
			}
			catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);
			}
			finally {
				_lock.Release();
			}
		}

		internal async Task ReassertAsync() {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				HashSet<uint> allActive = GetAllActiveGameIDs();

				if (allActive.Count == 0) {
					return;
				}

				if (_bot.IsConnectedAndLoggedOn) {
					uint[] activeIds = [.. allActive];
					_bot.ArchiLogger.LogGenericInfo($"{Command}: bot {_bot.BotName} logged on, re-asserting {activeIds.Length} game(s) ({DescribeGames(activeIds)}).");

					(bool success, string message) = await _bot.Actions.Play(activeIds).ConfigureAwait(false);

					if (!success) {
						_bot.ArchiLogger.LogGenericWarning($"{Command}: failed to re-assert games on {_bot.BotName} upon login - {message}");
					}
				}
			}
			catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);
			}
			finally {
				_lock.Release();
			}
		}

		internal async Task<string> CancelAsync(HashSet<uint>? appIDs) {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				if (_tasks.IsEmpty && _detachedGames.IsEmpty) {
					return Format(_bot, $"{Command}: no active timers to cancel.");
				}

				List<uint> cancelled = [];
				List<uint> alreadyDetached = [];
				List<uint> notFound = [];

				if ((appIDs == null) || (appIDs.Count == 0)) {
					if (_tasks.IsEmpty) {
						return Format(_bot, $"{Command}: no active timers to cancel (all running games are already detached).");
					}

					cancelled = [.. _tasks.Keys];

					foreach (uint id in cancelled) {
						if (_tasks.TryRemove(id, out GameTask? task)) {
							task.Dispose();
							_detachedGames[id] = 0;
						}
					}

					StopHeartbeat();
				} else {
					foreach (uint id in appIDs) {
						if (_tasks.TryRemove(id, out GameTask? task)) {
							task.Dispose();
							_detachedGames[id] = 0;
							cancelled.Add(id);
						} else if (_detachedGames.ContainsKey(id)) {
							alreadyDetached.Add(id);
						} else {
							notFound.Add(id);
						}
					}

					if (_tasks.IsEmpty) {
						StopHeartbeat();
					}
				}

				List<string> parts = [];

				if (cancelled.Count > 0) {
					parts.Add($"timers for {DescribeGames(cancelled)} cancelled, games keep running on Steam");
				}

				if (alreadyDetached.Count > 0) {
					parts.Add($"game(s) {DescribeGames(alreadyDetached)} already detached");
				}

				if (notFound.Count > 0) {
					parts.Add($"game(s) {DescribeGames(notFound)} not found in active timers");
				}

				return Format(_bot, $"{Command}: {string.Join("; ", (IEnumerable<string>) parts)}.");
			}
			finally {
				_lock.Release();
			}
		}

		internal async Task<string> StopAsync(HashSet<uint>? appIDs) {
			await _lock.WaitAsync().ConfigureAwait(false);

			try {
				if (_tasks.IsEmpty && _detachedGames.IsEmpty) {
					return Format(_bot, $"{Command}: no active games to stop.");
				}

				List<uint> stopped = [];
				List<uint> notFound = [];

				if ((appIDs == null) || (appIDs.Count == 0)) {
					stopped = [.. GetAllActiveGameIDs()];

					foreach (uint id in _tasks.Keys) {
						if (_tasks.TryRemove(id, out GameTask? task)) {
							task.Dispose();
						}
					}

					_detachedGames.Clear();
				} else {
					foreach (uint id in appIDs) {
						bool found = false;

						if (_tasks.TryRemove(id, out GameTask? task)) {
							task.Dispose();
							found = true;
						}

						if (_detachedGames.TryRemove(id, out _)) {
							found = true;
						}

						if (found) {
							stopped.Add(id);
						} else {
							notFound.Add(id);
						}
					}
				}

				HashSet<uint> remaining = GetAllActiveGameIDs();

				if (remaining.Count > 0) {
					if (_tasks.IsEmpty) {
						StopHeartbeat();
					}

					uint[] remainingIds = [.. remaining];
					(bool success, string message) = await _bot.Actions.Play(remainingIds).ConfigureAwait(false);

					if (!success) {
						_bot.ArchiLogger.LogGenericWarning($"{Command}: failed to reconcile games on {_bot.BotName} after stopping {DescribeGames(stopped)} - {message}");
					}
				} else {
					StopHeartbeat();
					await StopAllGamesAndRestoreFarmingAsync().ConfigureAwait(false);
				}

				List<string> parts = [];

				if (stopped.Count > 0) {
					if (remaining.Count == 0) {
						parts.Add($"stopped {DescribeGames(stopped)}, all games stopped, cards farming state restored");
					} else {
						parts.Add($"stopped {DescribeGames(stopped)}, remaining active games: {DescribeGames(remaining)}");
					}
				}

				if (notFound.Count > 0) {
					parts.Add($"game(s) {DescribeGames(notFound)} not found in active games");
				}

				return Format(_bot, $"{Command}: {string.Join("; ", (IEnumerable<string>) parts)}.");
			}
			finally {
				_lock.Release();
			}
		}

		internal string GetStatus() {
			List<GameTask> tasks = _tasks.Values.OrderBy(static t => t.DueAt).ToList();
			List<uint> detached = [.. _detachedGames.Keys];

			if ((tasks.Count == 0) && (detached.Count == 0)) {
				return Format(_bot, $"{Command}: no active timers.");
			}

			TimeSpan heartbeatIn = _nextHeartbeat - DateTime.UtcNow;

			if (heartbeatIn < TimeSpan.Zero) {
				heartbeatIn = TimeSpan.Zero;
			}

			string heartbeatText = (tasks.Count > 0) ? $", heartbeat in {FormatDuration(heartbeatIn)}" : "";

			if ((tasks.Count == 1) && (detached.Count == 0)) {
				GameTask t = tasks[0];

				return Format(_bot, $"{Command}: {t.AppID} -> stopping in {FormatDuration(t.Remaining)} ({t.DueAt:yyyy-MM-dd HH:mm:ss} UTC){heartbeatText}");
			}

			if ((tasks.Count == 0) && (detached.Count == 1)) {
				return Format(_bot, $"{Command}: {detached[0]} -> detached (running on Steam without timer)");
			}

			List<string> lines = [
				Format(_bot, $"{Command}: {tasks.Count} timed game(s), {detached.Count} detached game(s){heartbeatText}:")
			];

			foreach (GameTask t in tasks) {
				lines.Add($"  - AppID {t.AppID} -> stopping in {FormatDuration(t.Remaining)} ({t.DueAt:yyyy-MM-dd HH:mm:ss} UTC)");
			}

			foreach (uint id in detached) {
				lines.Add($"  - AppID {id} -> detached (running on Steam without timer)");
			}

			return string.Join(Environment.NewLine, (IEnumerable<string>) lines);
		}

		private void EnsureHeartbeatRunning() {
			_nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;

			if (_heartbeatTimer == null) {
				_heartbeatTimer = new Timer(
					_ => _ = HeartbeatAsync(),
					null,
					HeartbeatInterval,
					HeartbeatInterval
				);
			}
		}

		private void StopHeartbeat() {
			Timer? heartbeatTimer = _heartbeatTimer;
			_heartbeatTimer = null;
			heartbeatTimer?.Dispose();
		}

		private async Task StopAllGamesAndRestoreFarmingAsync() {
			try {
				(bool stopSuccess, string stopMessage) = await _bot.Actions.Play([]).ConfigureAwait(false);

				if (!stopSuccess) {
					_bot.ArchiLogger.LogGenericWarning($"{Command}: all games finished on {_bot.BotName} but stopping them failed - {stopMessage}");
				}

				string cardsState = _initialCardsPaused ? "left paused as it was" : _bot.Actions.Resume().Message;

				_bot.ArchiLogger.LogGenericInfo($"{Command}: all games finished on {_bot.BotName}, games stopped, cards farming {cardsState}.");
			}
			catch (Exception e) {
				_bot.ArchiLogger.LogGenericException(e);
			}
		}

		public void Dispose() {
			StopHeartbeat();

			foreach (uint id in _tasks.Keys) {
				if (_tasks.TryRemove(id, out GameTask? task)) {
					task.Dispose();
				}
			}

			_detachedGames.Clear();

			_lock.Dispose();
		}
	}
}
