using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;

namespace STS2_MCP;

/// <summary>
/// STS2_MCP.conf and the API token: loading the config at startup, generating and saving a token,
/// keeping the file readable by its owner only, and checking each request's token.
/// </summary>
public static partial class McpMod
{
    public const int DefaultPort = 15526;
    private const string ConfigFileName = "STS2_MCP.conf";

    /// <summary>When set to a non-blank value, replaces the `token` in STS2_MCP.conf.</summary>
    private const string TokenEnvironmentVariable = "STS2_MCP_TOKEN";

    /// <summary>The conf holds the API token, so on macOS and Linux it is 0600: its owner may read and write it.</summary>
    private const UnixFileMode ConfigFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    // Set from STS2_MCP.conf by Initialize() before the listener starts, and never changed after
    // that. Read by every request on ThreadPool threads, hence volatile.
    private static volatile string? _authToken;
    private static volatile bool _allowHiddenState;
    private static volatile bool _allowDebugActions;

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
                TightenConfigPermissions(configPath);
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
    /// holds the API token, so on macOS and Linux it is never on disk readable by anyone but its
    /// owner, not even for the moment between a write and a chmod: the JSON goes into a new file
    /// that is created 0600, which then replaces the conf in one rename.
    /// </summary>
    private static void WriteConfig(string configPath, JsonObject config)
    {
        string json = config.ToJsonString(_jsonOptionsPretty);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(configPath, json);
            return;
        }

        // A unique name, so CreateNew really creates it: the mode only applies to a new file, and a
        // leftover would keep whatever mode it had through a truncate.
        string tempPath = $"{configPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = System.IO.FileAccess.Write,
                UnixCreateMode = ConfigFileMode,
            };
            using (var stream = new FileStream(tempPath, options))
                stream.Write(Encoding.UTF8.GetBytes(json));   // UTF-8 without a BOM, like File.WriteAllText
            File.Move(tempPath, configPath, overwrite: true);
        }
        finally
        {
            // Already gone after the move; still there only when the write or the move failed.
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Narrows an existing conf to 0600 when it grants anything more (a hand-made file at the usual
    /// 0644, say, holding a token the user pasted in). A conf that is already as tight or tighter
    /// (0400) is left as it is, and so is its content, whether or not it parses.
    /// </summary>
    private static void TightenConfigPermissions(string configPath)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            if ((File.GetUnixFileMode(configPath) & ~ConfigFileMode) != 0)
                File.SetUnixFileMode(configPath, ConfigFileMode);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            GD.PrintErr($"[STS2 MCP] Could not make {configPath} readable by its owner only: {ex.Message}");
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
}
