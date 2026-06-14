using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace StayVibin.Services;

/// <summary>
/// Owns the bundled StayVibin Engine process (the Ollama fork). The app keeps this
/// separate from the OpenHands agent-server: the engine serves models/tools on
/// 11500, while the agent-server serves conversations on the configured app port.
/// </summary>
public sealed class StayVibinEngineManager : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 11500;
    public const string DefaultBaseUrl = "http://127.0.0.1:11500";

    public string Host { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{Host}:{Port}";
    public string ExecutablePath { get; }
    public string? LogFilePath { get; private set; }

    private readonly int _contextLength;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _logLock = new();
    private Process? _process;
    private StreamWriter? _logWriter;

    public event Action<string>? LogLine;

    public StayVibinEngineManager(
        string host = DefaultHost,
        int port = DefaultPort,
        string? executablePath = null,
        int contextLength = AppSettings.FallbackContextLength)
    {
        Host = host;
        Port = port;
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? DefaultExecutablePath()
            : executablePath!;
        _contextLength = contextLength;
    }

    public static string DefaultExecutablePath()
        => Path.Combine(AppContext.BaseDirectory, "Engine", "stayvibin-engine.exe");

    public static bool IsDefaultEngineUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var u)
               && (u.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                   || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
               && u.Port == DefaultPort;
    }

    public bool ExecutableExists => File.Exists(ExecutablePath);

    public async Task<bool> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // Reuse an already-running engine ONLY when it was launched with the same
        // context length we want now. If a previous run (or a pre-settings-change
        // session) left an engine listening at a different OLLAMA_CONTEXT_LENGTH,
        // reusing it is what causes the model to reload mid-session: internal calls
        // that omit num_ctx use the engine's launch default while the agent sends a
        // different num_ctx, and the resulting reload intermittently crashes the
        // CUDA runner ("shared object initialization failed"). When the context
        // differs we stop the stale engine and relaunch at the correct size.
        if (await IsHealthyAsync(ct))
        {
            if (RunningContextLength() == _contextLength)
                return true;
            Log($"[engine] running engine context != desired ({RunningContextLength()} "
                + $"vs {_contextLength}); restarting to keep context stable");
        }

        if (!ExecutableExists)
        {
            Log($"[engine] stayvibin-engine.exe not found at {ExecutablePath}");
            return false;
        }

        StopExistingEngineProcesses();
        await WaitUntilUnhealthyAsync(TimeSpan.FromSeconds(5), ct);

        OpenLogFile();

        var exeDir = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = "serve",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = exeDir
        };

        var host = $"{Host}:{Port}";
        psi.Environment["STAYVIBIN_HOST"] = host;
        psi.Environment["OLLAMA_HOST"] = host;
        psi.Environment["OLLAMA_CONTEXT_LENGTH"] = _contextLength.ToString();
        psi.Environment["OLLAMA_GO_TEMPLATE"] = "1";
        // Explicitly pin these to safe defaults instead of just leaving them
        // unset. The child process inherits the user's environment, and a
        // leftover persistent OLLAMA_KV_CACHE_TYPE=q4_0 / OLLAMA_FLASH_ATTENTION
        // from prior experiments would otherwise still apply. A q4_0 KV cache
        // (which also forces flash attention) changes the CUDA memory layout and,
        // on a model reload under VRAM pressure, can crash the runner with
        // "CUDA error: shared object initialization failed". f16 is the standard,
        // stable KV cache; we let the engine auto-decide flash attention per model
        // by passing an empty value (overriding any inherited setting).
        psi.Environment["OLLAMA_KV_CACHE_TYPE"] = "f16";
        psi.Environment["OLLAMA_FLASH_ATTENTION"] = "";
        psi.Environment["OLLAMA_MODELS"] = DefaultModelsDir();
        // This is a single-GPU desktop app. Keep exactly one model resident and a
        // single inference slot so two CUDA contexts never load at once. Without
        // these, a background warm/probe and an agent turn can each spin up a
        // runner, and the second CUDA init can fault ("shared object
        // initialization failed") under VRAM pressure, hanging the agent.
        psi.Environment["OLLAMA_MAX_LOADED_MODELS"] = "1";
        psi.Environment["OLLAMA_NUM_PARALLEL"] = "1";
        // Keep the model resident for the whole session. With the default 5m
        // keep-alive, a single long thinking/agent turn can let the model unload
        // while idle and then reload on the next call - and that reload is exactly
        // the CUDA re-init that can fault. Pinning it loaded avoids the churn.
        psi.Environment["OLLAMA_KEEP_ALIVE"] = "-1";

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };

        Log($"[engine] starting {ExecutablePath} serve on {host} (ctx={_contextLength})");
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // Record the context this engine was launched with so a later StartAsync
        // (e.g. after the user changes the context setting, or on the next app run)
        // can detect a mismatch and relaunch instead of reusing a stale instance.
        WriteContextSentinel(_contextLength);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                Log($"[engine] process exited early with code {_process.ExitCode}");
                return false;
            }
            if (await IsHealthyAsync(ct))
            {
                Log($"[engine] healthy at {BaseUrl}");
                return true;
            }
            await Task.Delay(500, ct);
        }

        Log("[engine] timed out waiting for /api/version");
        return false;
    }

    // Path of a tiny sentinel recording the context length the live engine was
    // launched with. Lives in the app-data root alongside logs.
    private static string ContextSentinelPath =>
        Path.Combine(AppPaths.Root, "engine.ctx");

    private static void WriteContextSentinel(int contextLength)
    {
        try { File.WriteAllText(ContextSentinelPath, contextLength.ToString()); }
        catch { /* best-effort: a missing sentinel just forces a safe relaunch */ }
    }

    /// <summary>
    /// Context length the currently-running engine was launched with, read from the
    /// sentinel. Returns -1 when unknown (no sentinel / unreadable), which never
    /// equals a real context so an engine of unknown provenance is relaunched.
    /// </summary>
    private static int RunningContextLength()
    {
        try
        {
            if (File.Exists(ContextSentinelPath)
                && int.TryParse(File.ReadAllText(ContextSentinelPath).Trim(), out var v))
                return v;
        }
        catch { /* fall through to unknown */ }
        return -1;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/api/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                Log("[engine] stopping");
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch { /* best-effort teardown */ }
        finally
        {
            _process?.Dispose();
            _process = null;
            lock (_logLock)
            {
                try { _logWriter?.Flush(); _logWriter?.Dispose(); } catch { }
                _logWriter = null;
            }
        }
    }

    private static string DefaultModelsDir()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, ".ollama", "models");
    }

    private async Task WaitUntilUnhealthyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsHealthyAsync(ct)) return;
            await Task.Delay(250, ct);
        }
    }

    private void StopExistingEngineProcesses()
    {
        var expected = Path.GetFullPath(ExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(ExecutablePath);
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            try
            {
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }
                if (string.IsNullOrWhiteSpace(path)
                    || !Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    continue;

                Log($"[engine] killing stale {processName} pid={proc.Id}");
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log($"[engine] failed to kill stale server pid={proc.Id}: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private void OpenLogFile()
    {
        try
        {
            LogFilePath = AppPaths.NewEngineLogPath();
            _logWriter = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        }
        catch
        {
            _logWriter = null;
            LogFilePath = null;
        }
    }

    private void Log(string line)
    {
        LogLine?.Invoke(line);
        lock (_logLock)
        {
            try { _logWriter?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}
