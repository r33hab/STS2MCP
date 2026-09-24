using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    ///
    /// 2: battle.enemies is no longer "the living enemies". A corpse the game keeps in the
    ///    fight (a Decimillipede segment waiting to reattach) is now listed with
    ///    alive:false, so a client that treated every entry as a target has to filter on
    ///    the new `alive` field.
    ///
    /// 3: map.next_encounters is gone. It reported which monster / elite / event the act would
    ///    serve NEXT, read out of the pre-rolled RoomSet - information the game does not show
    ///    until the room is entered, so it let a client route around matchups no player could
    ///    see. map.encounters_seen replaces it with what this act has ALREADY served, which is
    ///    what the map's own hover tips show for travelled nodes.
    ///
    /// 4: player.draw_pile_ordered is gone from the default payload. The draw pile's true order
    ///    is hidden information, now sent only to an opted-in `?hidden=1` read (with
    ///    allow_hidden_state in STS2_MCP.conf), and every request needs the API token. The same
    ///    version adds what a simulator needs to rebuild a combat position (enemy move ids, AI
    ///    history, AI fields, slots and intent damage; card enchantments, afflictions and cost
    ///    overrides; pile card ids; relic status and counters; battle.history), so a client that
    ///    relies on those can require 4.
    /// </summary>
    public const int StateSchemaVersion = 4;

    public const int DefaultPort = 15526;
    private const string ConfigFileName = "STS2_MCP.conf";

    /// <summary>When set to a non-blank value, replaces the `token` in STS2_MCP.conf.</summary>
    private const string TokenEnvironmentVariable = "STS2_MCP_TOKEN";

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

    // Set from STS2_MCP.conf by Initialize() before the listener starts, and never changed after
    // that. Read by every request on ThreadPool threads, hence volatile.
    private static volatile string? _authToken;
    private static volatile bool _allowHiddenState;
    private static volatile bool _allowDebugActions;

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

    /// <summary>What STS2_MCP.conf configures. Read once at startup; a change needs a game restart.</summary>
    private sealed record ModConfig(int Port, string Token, bool AllowHiddenState, bool AllowDebugActions);

    /// <summary>
    /// Reads STS2_MCP.conf, the JSON file next to the mod DLL: `port` (default 15526), `token`,
    /// and the two opt-in gates `allow_hidden_state` and `allow_debug_actions`.
    ///
    /// The token is what stops every other program on this machine - a web page included - from
    /// reading or driving the game. STS2_MCP_TOKEN in the environment takes precedence over the
    /// file. With neither, a random token is generated and written back into the file, every other
    /// key kept as it was, because the file is where clients read it from.
    ///
    /// A file that exists but is not a JSON object is reported and never rewritten: that would wipe
    /// whatever the user was in the middle of editing. The mod then runs on defaults with a token no
    /// client knows, so every request is refused until the file is fixed.
    /// </summary>
    private static ModConfig LoadConfig()
    {
        try
        {
            string? modDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            string? configPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, ConfigFileName);

            // null when the file cannot be used; a missing file starts from the default config,
            // which is written out below.
            JsonObject? config = null;
            bool writeBack = false;
            if (configPath != null && !File.Exists(configPath))
            {
                config = new JsonObject { ["port"] = DefaultPort };
                writeBack = true;
            }
            else if (configPath != null)
            {
                config = ReadConfigObject(configPath);
            }

            int port = DefaultPort;
            if (config != null)
            {
                if (config["port"] is JsonValue portValue
                    && portValue.GetValueKind() == JsonValueKind.Number
                    && portValue.TryGetValue(out int configuredPort)
                    && configuredPort is > 0 and <= 65535)
                {
                    port = configuredPort;
                }
                else
                {
                    GD.PrintErr($"[STS2 MCP] Invalid or missing 'port' in {configPath}, using default {DefaultPort}");
                }
            }

            bool tokenGenerated = false;
            string? token = System.Environment.GetEnvironmentVariable(TokenEnvironmentVariable)?.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                GD.Print($"[STS2 MCP] Using the API token from {TokenEnvironmentVariable}");
            }
            else if (ReadConfigToken(config) is { } configuredToken)
            {
                token = configuredToken;
            }
            else
            {
                token = GenerateToken();
                tokenGenerated = true;
                if (config != null)
                {
                    config["token"] = token;
                    writeBack = true;
                }
                else
                {
                    GD.PrintErr($"[STS2 MCP] No usable {ConfigFileName} to save an API token in; every request "
                        + $"will be refused until it has a 'token' or {TokenEnvironmentVariable} is set");
                }
            }

            if (writeBack && config != null && configPath != null)
            {
                try
                {
                    WriteConfig(configPath, config);
                    GD.Print(tokenGenerated
                        ? $"[STS2 MCP] Generated an API token and saved it to {configPath}"
                        : $"[STS2 MCP] Created default config at {configPath}");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    GD.PrintErr(tokenGenerated
                        ? $"[STS2 MCP] Could not save the generated API token to {configPath} ({ex.Message}); every "
                            + $"request will be refused until it has a 'token' or {TokenEnvironmentVariable} is set"
                        : $"[STS2 MCP] No config found at {configPath}; using default port {DefaultPort}");
                }
            }

            return new ModConfig(
                port,
                token,
                AllowHiddenState: IsConfigFlagSet(config, "allow_hidden_state"),
                AllowDebugActions: IsConfigFlagSet(config, "allow_debug_actions"));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to load config: {ex.Message}, using default port {DefaultPort}; "
                + "every request will be refused until it loads");
            return new ModConfig(DefaultPort, GenerateToken(), AllowHiddenState: false, AllowDebugActions: false);
        }
    }

    /// <summary>The conf as a JSON object, or null (reported) when it cannot be read as one.</summary>
    private static JsonObject? ReadConfigObject(string configPath)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(configPath)) is JsonObject config)
            {
                // Materialise now, so a duplicate key throws here rather than halfway through the reads.
                _ = config.Count;
                return config;
            }
            GD.PrintErr($"[STS2 MCP] {configPath} is not a JSON object; using defaults and leaving the file as it is");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Could not read {configPath} ({ex.Message}); using defaults and leaving the file as it is");
        }
        return null;
    }

    /// <summary>The conf's `token`, trimmed; null when it is missing, not a string, or blank.</summary>
    private static string? ReadConfigToken(JsonObject? config)
    {
        if (config?["token"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return null;
        string token = value.GetValue<string>().Trim();
        return token.Length > 0 ? token : null;
    }

    /// <summary>A gate is on only for a literal JSON <c>true</c>; "true", 1 or a typo leave it off.</summary>
    private static bool IsConfigFlagSet(JsonObject? config, string key)
        => config?[key]?.GetValueKind() == JsonValueKind.True;

    /// <summary>
    /// 256 bits from the OS's cryptographic RNG, as 64 lowercase hex characters. Spelled out in
    /// full because Godot.RandomNumberGenerator, a non-cryptographic PCG, shares the short name.
    /// </summary>
    private static string GenerateToken()
        => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The config file is edited by hand, so it stays indented regardless of the API default. It
    /// holds the API token, so on macOS and Linux only its owner may read it.
    /// </summary>
    private static void WriteConfig(string configPath, JsonObject config)
    {
        File.WriteAllText(configPath, config.ToJsonString(_jsonOptionsPretty));
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            GD.PrintErr($"[STS2 MCP] Could not make {configPath} readable by its owner only: {ex.Message}");
        }
    }

    public static void Initialize()
    {
        try
        {
            // Optional settings UI patches should not block the HTTP bridge itself.
            TryApplyHarmonyPatches();

            // Expose a completion signal for async room entry; see TrackRoomEntries.
            TrackRoomEntries();

            // Connect to main thread process frame for action execution
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(ProcessMainThreadQueue));

            var config = LoadConfig();
            // In place before the listener accepts anything, so no request is ever checked
            // against a token or gate that has not been loaded yet.
            _authToken = config.Token;
            _allowHiddenState = config.AllowHiddenState;
            _allowDebugActions = config.AllowDebugActions;

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{config.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{config.Port}/");
            _listener.Start();

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "STS2_MCP_Server"
            };
            _serverThread.Start();

            GD.Print($"[STS2 MCP] v{VersionString} server started on http://localhost:{config.Port}/");
            if (config.AllowHiddenState)
                GD.Print("[STS2 MCP] allow_hidden_state is on: a GET with ?hidden=1 reports the true draw order and the run seed");
            if (config.AllowDebugActions)
                GD.Print("[STS2 MCP] allow_debug_actions is on: debug_* actions can rewrite the run");
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

            // No web page may use this API (enderzcx 7baafb1). Browsers attach Origin to every
            // cross-origin fetch and to every POST; the coach, curl and the MCP bridge never
            // send it. What a page can send without Origin (an <img> or <script> GET) cannot
            // carry the token header, and no CORS headers are sent, so a page cannot read a reply.
            if (!string.IsNullOrEmpty(request.Headers["Origin"]))
            {
                SendError(response, 403, "Browser origins are not permitted", pretty);
                return;
            }

            if (!IsAuthorized(request))
            {
                SendError(response, 401, "Missing or invalid token", pretty);
                return;
            }

            // Every POST handler parses a JSON body. Insisting on the JSON media type also rules
            // out the bodies an HTML form can send (form-encoded, multipart, text/plain).
            if (request.HttpMethod == "POST" && !IsJsonContentType(request.ContentType))
            {
                SendError(response, 415, "POST bodies must be application/json", pretty);
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

    /// <summary>
    /// Whether the request carries the API token, as `X-STS2-Token: &lt;token&gt;` or
    /// `Authorization: Bearer &lt;token&gt;`. The comparison takes the same time wherever the first
    /// wrong byte is, so response timing cannot be used to guess the token byte by byte. With no
    /// token loaded nothing is authorized.
    /// </summary>
    private static bool IsAuthorized(HttpListenerRequest request)
    {
        string? expected = _authToken;
        if (string.IsNullOrEmpty(expected))
            return false;

        string? presented = request.Headers["X-STS2-Token"]?.Trim();
        string? authorization = request.Headers["Authorization"];
        if (presented == null && authorization != null
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = authorization.Substring("Bearer ".Length).Trim();
        }
        if (presented == null)
            return false;

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presented);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return presentedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes);
    }

    /// <summary>`application/json`, with or without parameters such as `; charset=utf-8`.</summary>
    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;
        int parameters = contentType.IndexOf(';');
        string mediaType = (parameters >= 0 ? contentType[..parameters] : contentType).Trim();
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase);
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
        bool includeHiddenState = WantsHiddenState(request);

        try
        {
            var state = RunOnMainThreadBlocking(() => BuildGameState(includeHiddenState));

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

        // Debug and staging actions rewrite the run instead of playing it; they stay refused
        // unless STS2_MCP.conf opts in with allow_debug_actions.
        if (IsDebugAction(action) && !_allowDebugActions)
        {
            SendError(response, 403, "debug actions disabled", pretty);
            return;
        }

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
