using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace STS2_MCP;

[ModInitializer("Initialize")]
public static partial class McpMod
{
    // Keep in step with <Version> in STS2_MCP.csproj and "version" in mod_manifest.json.
    public const string Version = "0.4.0";

    /// <summary>
    /// Version of the state/action contract, reported as `schema_version` on every payload.
    ///
    /// Bumped only when an existing field or action changes meaning or disappears - the cases
    /// where a client that was written against the old shape is now wrong. Adding fields,
    /// actions, parameters or state_types does not bump it, so clients can keep feature-detecting
    /// additions by presence and use this only to notice a breaking change.
    /// </summary>
    public const int StateSchemaVersion = 1;

    public const int DefaultPort = 15526;
    private const string ConfigFileName = "STS2_MCP.conf";

    /// <summary>
    /// How long an HTTP request waits for the game's main thread before giving up (503).
    ///
    /// A click-sized action resolves within the frame it is dequeued in, so this is never
    /// reached while the game renders; ten seconds only elapses when frames have stopped
    /// (scene load, hang, the OS pausing the app) and no wait would ever succeed.
    /// </summary>
    internal const int MainThreadTimeoutMs = 10_000;

    /// <summary>
    /// How many actions may sit in the main-thread queue before new requests are rejected (503).
    ///
    /// The queue is drained ten per frame, so a healthy game never accumulates this many:
    /// 32 pending items means nobody is draining it, and enqueueing more would only pile up
    /// stale actions that all fire at once when frames resume.
    /// </summary>
    internal const int MainThreadQueueLimit = 32;

    /// <summary>How often the settle wait re-reads the state, in ms. Roughly four frames at 60 fps.</summary>
    private const int SettlePollIntervalMs = 60;

    /// <summary>
    /// How many consecutive polls must look settled before the wait returns.
    ///
    /// One is not enough: a click takes effect on the next frame at the earliest, and between
    /// two queued actions the game looks idle for a single frame. Every client that polls this
    /// API converged on the same rule after shipping mid-resolution states to its caller.
    /// </summary>
    private const int SettleConsecutivePolls = 3;

    /// <summary>
    /// How long a completely unchanged state is waited on before the wait gives up.
    ///
    /// An action the game silently ignored (a button that was not there, a click the UI ate)
    /// never changes anything, so waiting the full timeout for it only stalls the caller.
    /// </summary>
    private const int SettleNoChangeGiveUpMs = 3_000;

    private const int SettleDefaultTimeoutMs = 10_000;
    private const int SettleMinTimeoutMs = 500;
    private const int SettleMaxTimeoutMs = 30_000;

    private static string? _buildCommit;
    private static bool _buildCommitResolved;

    /// <summary>
    /// The git revision this assembly was built from (the part after '+' in
    /// InformationalVersion), or null when the build could not determine one.
    /// Lets a bug report name the exact build rather than just "0.4.0".
    /// </summary>
    internal static string? BuildCommit
    {
        get
        {
            if (_buildCommitResolved)
                return _buildCommit;

            _buildCommitResolved = true;
            try
            {
                var informational = typeof(McpMod).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                int plus = informational?.IndexOf('+') ?? -1;
                if (plus >= 0 && plus + 1 < informational!.Length)
                    _buildCommit = informational[(plus + 1)..];
            }
            catch
            {
                // Reflection over assembly attributes can fail in a trimmed or odd host;
                // an unknown commit is not worth failing a state read over.
                _buildCommit = null;
            }

            return _buildCommit;
        }
    }

    /// <summary>Version with the build commit appended when known: "0.4.0+abc1234".</summary>
    internal static string VersionString =>
        BuildCommit is { Length: > 0 } commit ? $"{Version}+{commit}" : Version;

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
    // Indentation was ~32% of a typical in-combat state response (12,069 -> 8,178 bytes).
    // The main consumer is an LLM that pays per token, so compact is the default and
    // ?pretty=1 opts back in. Two immutable instances: JsonSerializerOptions must not be
    // mutated after first use, and requests are served concurrently on ThreadPool threads.
    internal static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static readonly JsonSerializerOptions _jsonOptionsPretty = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int LoadPort()
    {
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (modDir == null) return DefaultPort;

            string configPath = Path.Combine(modDir, ConfigFileName);
            if (!File.Exists(configPath))
            {
                try
                {
                    var defaultConfig = new Dictionary<string, object> { ["port"] = DefaultPort };
                    // The config file is edited by hand, so it stays indented regardless of the API default.
                    string json = JsonSerializer.Serialize(defaultConfig, _jsonOptionsPretty);
                    File.WriteAllText(configPath, json);
                    GD.Print($"[STS2 MCP] Created default config at {configPath}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    GD.Print($"[STS2 MCP] No config found at {configPath}; using default port {DefaultPort}");
                }
                return DefaultPort;
            }

            string content = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("port", out var portElem)
                && portElem.TryGetInt32(out int port)
                && port is > 0 and <= 65535)
            {
                return port;
            }

            GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
            return DefaultPort;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to load config: {ex.Message}, using default port {DefaultPort}");
            return DefaultPort;
        }
    }

    public static void Initialize()
    {
        try
        {
            // Optional settings UI patches should not block the HTTP bridge itself.
            TryApplyHarmonyPatches();

            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));

            int port = LoadPort();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{VersionString} server started on http://localhost:{port}/");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to start: {ex}");
        }
    }

    private static void TryApplyHarmonyPatches()
    {
        try
        {
            new Harmony("com.sts2mcp").PatchAll();
        }
        catch (Exception ex)
        {
            GD.Print(
                $"[STS2 MCP] Optional Harmony settings UI injection skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ProcessMainThreadQueue()
    {
        int processed = 0;
        while (_mainThreadQueue.TryDequeue(out var action) && processed < 10)
        {
            try { action(); }
            catch (Exception ex) { GD.PrintErr($"[STS2 MCP] Main thread action error: {ex}"); }
            processed++;
        }
    }

    internal static Task<T> RunOnMainThread<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    internal static Task RunOnMainThread(Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        _mainThreadQueue.Enqueue(() =>
        {
            try { action(); tcs.SetResult(true); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Per-request flag telling a queued closure that its caller has stopped waiting.</summary>
    private sealed class MainThreadCancellation
    {
        // volatile: written on the HTTP thread, read on the game's main thread.
        internal volatile bool Cancelled;
    }

    /// <summary>
    /// Runs <paramref name="func"/> on the game's main thread and waits for it.
    ///
    /// Every blocking HTTP handler goes through here instead of waiting on the task forever:
    /// the queue is drained from Godot's ProcessFrame signal, so when frames stop (scene load,
    /// hang, the OS pausing the app, a modal the engine blocks on) a plain wait never returns.
    /// That held a ThreadPool thread per request, and - worse - every request queued meanwhile
    /// still ran once frames resumed, so an agent that retried `end_turn` five times got five
    /// end_turns.
    /// </summary>
    /// <exception cref="MainThreadUnavailableException">
    /// The queue is already backed up, or the main thread did not answer in time. In both cases
    /// the work was NOT performed, so the caller may retry.
    /// </exception>
    internal static T RunOnMainThreadBlocking<T>(Func<T> func, int timeoutMs = MainThreadTimeoutMs)
    {
        int pending = _mainThreadQueue.Count;
        if (pending >= MainThreadQueueLimit)
        {
            throw new MainThreadUnavailableException(
                $"main thread queue is full ({pending} pending); the game is not processing frames");
        }

        var cancellation = new MainThreadCancellation();
        var tcs = new TaskCompletionSource<T>();
        _mainThreadQueue.Enqueue(() =>
        {
            // The client already gave up on this request; running the action now would apply a
            // command the caller has since retried or abandoned.
            if (cancellation.Cancelled) return;

            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });

        bool completed;
        try
        {
            completed = tcs.Task.Wait(timeoutMs);
        }
        catch (AggregateException ex) when (ex.InnerException != null)
        {
            // Unwrap so callers' `catch (Exception ex)` keeps showing the action's own message.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable; satisfies definite assignment
        }

        if (!completed)
        {
            // Unavoidable race: if the closure has already started running, setting this changes
            // nothing and the action completes anyway. A click-sized action resolves inside one
            // frame, so in practice a timeout means the closure never started.
            cancellation.Cancelled = true;
            throw new MainThreadUnavailableException(
                $"timed out after {timeoutMs} ms waiting for the game's main thread");
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    private static void ServerLoop()
    {
        while (_listener?.IsListening == true)
        {
            try
            {
                var context = _listener.GetContext();
                // Handle each request asynchronously so we don't block the listener
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            bool pretty = WantsPretty(request);
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (path == "/")
            {
                // A dictionary, not an anonymous type: _jsonOptions drops null *properties*,
                // and "commit" has to stay present (as null) for clients that read it blindly.
                SendJson(response, new Dictionary<string, object?>
                {
                    ["message"] = $"Hello from STS2 MCP v{VersionString}",
                    ["status"] = "ok",
                    ["version"] = Version,
                    ["commit"] = BuildCommit,
                    ["schema_version"] = StateSchemaVersion
                }, pretty);
            }
            else if (path == "/api/v1/singleplayer")
            {
                // Hard-block singleplayer endpoint during multiplayer runs
                // to prevent calling the non-sync-safe end_turn path
                if (IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Multiplayer run is active. Use /api/v1/multiplayer instead.", pretty);
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostAction(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (path == "/api/v1/multiplayer")
            {
                // Guard: reject multiplayer endpoint during singleplayer runs
                if (!IsMultiplayerRun())
                {
                    SendError(response, 409,
                        "Not in a multiplayer run. Use /api/v1/singleplayer instead.", pretty);
                    return;
                }

                if (request.HttpMethod == "GET")
                    HandleGetMultiplayerState(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostMultiplayerAction(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (path == "/api/v1/profiles")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfiles(request, response);
                else if (request.HttpMethod == "POST")
                    HandlePostProfiles(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (path == "/api/v1/profile")
            {
                if (request.HttpMethod == "GET")
                    HandleGetProfile(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (path == "/api/v1/compendium")
            {
                if (request.HttpMethod == "GET")
                    HandleGetCompendium(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else if (path == "/api/v1/wiki")
            {
                if (request.HttpMethod == "GET")
                    HandleGetWiki(request, response);
                else
                    SendError(response, 405, "Method not allowed", pretty);
            }
            else
            {
                SendError(response, 404, "Not found", pretty);
            }
        }
        // Safety net for any blocking call reached outside a handler's own try/catch: a stalled
        // main thread must never be reported as an internal error.
        catch (MainThreadUnavailableException ex)
        {
            try { SendUnavailable(context.Response, ex.Message, WantsPretty(context.Request)); }
            catch { /* response may already be closed */ }
        }
        catch (Exception ex)
        {
            try
            {
                SendError(context.Response, 500, $"Internal error: {ex.Message}",
                    WantsPretty(context.Request));
            }
            catch { /* response may already be closed */ }
        }
    }

    // Called on HTTP thread (not main thread) as a best-effort guard.
    // The try/catch handles race conditions during run transitions.
    // Authoritative checks happen inside RunOnMainThread lambdas.
    internal static bool IsMultiplayerRun()
    {
        try
        {
            return MegaCrit.Sts2.Core.Runs.RunManager.Instance.IsInProgress
                && MegaCrit.Sts2.Core.Runs.RunManager.Instance.NetService.Type.IsMultiplayer();
        }
        catch { return false; }
    }

    private static void HandleGetMultiplayerState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";
        bool pretty = WantsPretty(request);

        try
        {
            var state = RunOnMainThreadBlocking(() => BuildMultiplayerGameState());

            if (format == "markdown")
            {
                string md = FormatAsMarkdown(state);
                SendText(response, md, "text/markdown");
            }
            else
            {
                SendJson(response, state, pretty);
            }
        }
        catch (MainThreadUnavailableException ex)
        {
            try { SendUnavailable(response, ex.Message, pretty); }
            catch { /* response may be unusable */ }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetMultiplayerState: {ex}");
            try
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to read multiplayer game state: {ex.Message}",
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace
                }, pretty);
            }
            catch { /* response may be unusable */ }
        }
    }

    private static void HandlePostMultiplayerAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON", pretty);
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field", pretty);
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Menu actions (FTUE/popup dismissal, game-over, character select, etc.) are
        // scene-tree-driven and equally valid in MP. Route them to the shared handler
        // so MP clients can dismiss blocking FTUE prompts without going through the
        // run-mode-specific dispatcher.
        if (action == "menu_select")
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                int? ascension = ReadOptionalInt(parsed, "ascension");
                var result = RunOnMainThreadBlocking(() => ExecuteMenuSelect(option, seed, ascension));
                SendJson(response, EnsureStatus(result), pretty);
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}", pretty);
            }
            return;
        }

        try
        {
            var result = RunOnMainThreadBlocking(() => ExecuteMultiplayerAction(action, parsed));
            SendJson(response, EnsureStatus(result), pretty);
        }
        catch (MainThreadUnavailableException ex)
        {
            SendUnavailable(response, ex.Message, pretty);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Multiplayer action failed: {ex.Message}", pretty);
        }
    }

    private static int? ReadOptionalInt(Dictionary<string, JsonElement> data, string key)
    {
        if (!data.TryGetValue(key, out var elem))
            return null;
        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int number))
            return number;
        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out int parsedNumber))
            return parsedNumber;
        return null;
    }

    private static void HandleGetState(HttpListenerRequest request, HttpListenerResponse response)
    {
        string format = request.QueryString["format"] ?? "json";
        bool pretty = WantsPretty(request);

        try
        {
            var state = RunOnMainThreadBlocking(() => BuildGameState());

            if (format == "markdown")
            {
                try
                {
                    SendText(response, FormatAsMarkdown(state), "text/markdown");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[STS2 MCP] FormatAsMarkdown failed, returning JSON: {ex}");
                    SendJson(response, state, pretty);
                }
            }
            else
            {
                SendJson(response, state, pretty);
            }
        }
        catch (MainThreadUnavailableException ex)
        {
            try { SendUnavailable(response, ex.Message, pretty); }
            catch { /* response may be unusable */ }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] HandleGetState: {ex}");
            try
            {
                response.StatusCode = 500;
                SendJson(response, new Dictionary<string, object?>
                {
                    ["error"] = $"Failed to read game state: {ex.Message}",
                    ["exception_type"] = ex.GetType().FullName,
                    ["stack_trace"] = ex.StackTrace
                }, pretty);
            }
            catch { /* response may be unusable */ }
        }
    }

    /// <summary>
    /// `true` / `1` / `"1"` / `"true"` / `"yes"` / `"on"` in the body, or the same in the query
    /// string (`?wait=1`, or a bare `?wait`). Anything else is false; a missing key is
    /// <paramref name="fallback"/>.
    /// </summary>
    private static bool ReadFlagParam(
        Dictionary<string, JsonElement> data, HttpListenerRequest request, string key, bool fallback)
    {
        if (data.TryGetValue(key, out var elem))
        {
            switch (elem.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return elem.TryGetInt32(out int number) && number != 0;
                case JsonValueKind.String: return IsTruthyFlag(elem.GetString());
            }
        }

        string? query = request.QueryString[key];
        if (query != null)
            return IsTruthyFlag(query);

        // A valueless `?wait` is filed under the null key by HttpListener, not under "wait".
        string? valueless = request.QueryString[null];
        if (valueless != null && Array.IndexOf(valueless.Split(','), key) >= 0)
            return true;

        return fallback;
    }

    private static bool IsTruthyFlag(string? value)
    {
        if (value == null) return false;
        // `?wait=` with nothing after it still reads as "switch it on".
        if (value.Length == 0) return true;
        return value == "1"
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the game has finished acting on whatever was last asked of it.</summary>
    /// <remarks>
    /// Kept free of game types on purpose: it reads only the dictionary a <c>BuildGameState</c>
    /// produced, so it can be reasoned about (and tested) without a running game.
    /// </remarks>
    internal static bool IsSettledState(Dictionary<string, object?> state)
    {
        if (state.TryGetValue("is_resolving", out var resolving) && resolving is true)
            return false;

        string? stateType = state.GetValueOrDefault("state_type") as string;
        if (stateType is "monster" or "elite" or "boss")
        {
            // The enemies' turn drains the action queue in bursts, so `is_resolving` blinks false
            // between two enemy moves. Only the play phase means the caller may act again.
            if (state.GetValueOrDefault("battle") is not Dictionary<string, object?> battle)
                return false;
            if (battle.GetValueOrDefault("is_play_phase") is not true)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A cheap digest of everything an action could plausibly move: screen, floor, round, the
    /// player's hp/block/energy, the cards in hand, the enemies' hp/block, and how many options
    /// the current screen offers. Used only to tell "the action did nothing" from "the action
    /// landed", so it does not have to be exhaustive - just stable between two idle reads.
    /// </summary>
    internal static string StateFingerprint(Dictionary<string, object?> state)
    {
        var sb = new StringBuilder();
        sb.Append(state.GetValueOrDefault("state_type")).Append('|');
        sb.Append(state.GetValueOrDefault("menu_screen")).Append('|');

        if (state.GetValueOrDefault("run") is Dictionary<string, object?> run)
            sb.Append(run.GetValueOrDefault("floor"));
        sb.Append('|');

        if (state.GetValueOrDefault("battle") is Dictionary<string, object?> battle)
        {
            sb.Append(battle.GetValueOrDefault("round"))
              .Append(',').Append(battle.GetValueOrDefault("turn"))
              .Append(',').Append(battle.GetValueOrDefault("is_play_phase"));
            if (battle.GetValueOrDefault("enemies") is List<Dictionary<string, object?>> enemies)
            {
                foreach (var enemy in enemies)
                {
                    sb.Append(';').Append(enemy.GetValueOrDefault("entity_id"))
                      .Append(':').Append(enemy.GetValueOrDefault("hp"))
                      .Append('/').Append(enemy.GetValueOrDefault("block"));
                }
            }
        }
        sb.Append('|');

        if (state.GetValueOrDefault("player") is Dictionary<string, object?> player)
        {
            sb.Append(player.GetValueOrDefault("hp"))
              .Append('/').Append(player.GetValueOrDefault("block"))
              .Append('/').Append(player.GetValueOrDefault("energy"));
            if (player.GetValueOrDefault("hand") is List<Dictionary<string, object?>> hand)
            {
                foreach (var card in hand)
                    sb.Append(';').Append(card.GetValueOrDefault("uid"));
            }
        }
        sb.Append('|');

        if (state.GetValueOrDefault("options") is System.Collections.ICollection options)
            sb.Append(options.Count);

        return sb.ToString();
    }

    /// <summary>
    /// Polls the state from the HTTP thread until the game has settled, then appends
    /// `waited_ms` / `polls` / `settled` / `changed` (and `state`) to the action's own result.
    /// </summary>
    private static void AppendSettleResult(
        Dictionary<string, object?> result, string? before, int timeoutMs, bool includeState)
    {
        var elapsed = Stopwatch.StartNew();
        int polls = 0;
        int consecutiveSettled = 0;
        bool changed = false;
        bool settled = false;
        string? waitError = null;
        Dictionary<string, object?>? last = null;

        while (true)
        {
            Dictionary<string, object?>? state = null;
            try
            {
                state = RunOnMainThreadBlocking(() => BuildGameState());
            }
            catch (MainThreadUnavailableException ex)
            {
                // The action itself already ran; only our observation of it failed. Turning this
                // into a 503 would tell the caller "nothing was applied", which is a lie.
                waitError = ex.Message;
                break;
            }
            catch (Exception ex)
            {
                // BuildGameState throws while a scene is swapping (run and room models are torn
                // down mid-frame). That is the opposite of settled, so keep polling.
                waitError = ex.Message;
                consecutiveSettled = 0;
            }
            polls++;

            if (state != null)
            {
                last = state;
                waitError = null;
                if (before != null && StateFingerprint(state) != before)
                    changed = true;

                if (IsSettledState(state))
                {
                    if (++consecutiveSettled >= SettleConsecutivePolls)
                    {
                        settled = true;
                        break;
                    }
                }
                else
                {
                    consecutiveSettled = 0;
                }
            }

            // Nothing moved at all: the action was a no-op as far as the game is concerned, and
            // no amount of further waiting will change that.
            if (before != null && !changed && elapsed.ElapsedMilliseconds >= SettleNoChangeGiveUpMs)
            {
                settled = true;
                break;
            }

            if (elapsed.ElapsedMilliseconds >= timeoutMs)
                break;

            Thread.Sleep(SettlePollIntervalMs);
        }

        elapsed.Stop();
        result["waited_ms"] = (int)elapsed.ElapsedMilliseconds;
        result["polls"] = polls;
        result["settled"] = settled;
        result["changed"] = changed;
        if (waitError != null)
            result["wait_error"] = waitError;
        if (includeState && last != null)
            result["state"] = last;
    }

    /// <summary>
    /// Runs a singleplayer action on the main thread and, when the caller passed `wait`, keeps
    /// reading the state until the game has settled - so a single round trip answers both
    /// "was the action accepted?" and "what does the game look like now?".
    /// </summary>
    private static Dictionary<string, object?> ExecuteWithOptionalWait(
        HttpListenerRequest request, Dictionary<string, JsonElement> parsed,
        Func<Dictionary<string, object?>> action)
    {
        if (!ReadFlagParam(parsed, request, "wait", false))
            return EnsureStatus(RunOnMainThreadBlocking(action));

        int timeoutMs = Math.Clamp(
            ReadOptionalInt(parsed, "wait_timeout_ms")
                ?? (int.TryParse(request.QueryString["wait_timeout_ms"], out int q) ? q : SettleDefaultTimeoutMs),
            SettleMinTimeoutMs, SettleMaxTimeoutMs);
        bool includeState = ReadFlagParam(parsed, request, "include_state", true);

        // Snapshot before acting, so an action that changes nothing can be recognised instead of
        // being waited out. A MainThreadUnavailableException here is a genuine 503: nothing ran yet.
        string? before = null;
        try { before = StateFingerprint(RunOnMainThreadBlocking(() => BuildGameState())); }
        catch (MainThreadUnavailableException) { throw; }
        catch { /* an unreadable pre-state only costs us the no-op shortcut */ }

        var result = EnsureStatus(RunOnMainThreadBlocking(action));

        // Waiting on an action the game rejected would just burn the whole timeout.
        if (result.GetValueOrDefault("status") as string == "ok")
            AppendSettleResult(result, before, timeoutMs, includeState);

        return result;
    }

    private static void HandlePostAction(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON", pretty);
            return;
        }

        if (parsed == null || !parsed.TryGetValue("action", out var actionElem))
        {
            SendError(response, 400, "Missing 'action' field", pretty);
            return;
        }

        string action = actionElem.GetString() ?? "";

        // Revealing timeline epochs is a main-menu operation, so it must not require a run.
        if (action == "timeline_reveal_epochs")
        {
            try
            {
                SendJson(response,
                    ExecuteWithOptionalWait(request, parsed, () => ExecuteTimelineRevealEpochs()), pretty);
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Timeline reveal failed: {ex.Message}", pretty);
            }
            return;
        }

        // Handle menu actions separately (no run required)
        if (action == "menu_select")
        {
            try
            {
                var option = parsed.TryGetValue("option", out var optElem) ? optElem.GetString() ?? "" : "";
                var seed = parsed.TryGetValue("seed", out var seedElem) ? seedElem.GetString() : null;
                int? ascension = ReadOptionalInt(parsed, "ascension");
                var result = ExecuteWithOptionalWait(
                    request, parsed, () => ExecuteMenuSelect(option, seed, ascension));
                SendJson(response, result, pretty);
            }
            catch (MainThreadUnavailableException ex)
            {
                SendUnavailable(response, ex.Message, pretty);
            }
            catch (Exception ex)
            {
                SendError(response, 500, $"Menu action failed: {ex.Message}", pretty);
            }
            return;
        }

        try
        {
            var result = ExecuteWithOptionalWait(request, parsed, () => ExecuteAction(action, parsed));
            SendJson(response, result, pretty);
        }
        catch (MainThreadUnavailableException ex)
        {
            SendUnavailable(response, ex.Message, pretty);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Action failed: {ex.Message}", pretty);
        }
    }
}

/// <summary>
/// The game's main thread did not run the requested work: the queue was already backed up, or
/// the wait timed out. Distinct from an action failure so handlers can answer 503 + retry
/// instead of 500 - nothing was executed, and the same request is safe to send again.
/// </summary>
internal sealed class MainThreadUnavailableException : Exception
{
    internal MainThreadUnavailableException(string message) : base(message) { }
}
