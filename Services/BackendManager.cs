using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace StayVibin.Services;

/// <summary>
/// Owns the local AI engine child process: locates the executable, starts
/// it bound to localhost, waits until the HTTP health endpoint responds, and
/// guarantees the process is torn down when the app exits. Server stdout/stderr
/// is mirrored to a log file so errors survive after the window closes.
/// </summary>
public sealed class BackendManager : IDisposable
{
    public string Host { get; }
    public int Port { get; }
    public string BaseUrl => $"http://{Host}:{Port}";
    public string ExecutablePath { get; }
    public string? LogFilePath { get; private set; }

    /// <summary>
    /// Project directory the server should launch in. The engine's grep/glob tools
    /// resolve a RELATIVE 'path' argument (e.g. "src") against the server process's
    /// current directory, not the conversation workspace. Launching the server in
    /// the project folder makes those relative searches land in the project instead
    /// of the install dir, so the agent stops failing every relative-path search.
    /// Set this before <see cref="StartAsync"/>. Null falls back to the exe folder.
    /// </summary>
    public string? WorkingDir { get; set; }

    /// <summary>The directory the running server was actually launched in.</summary>
    public string? LaunchedWorkingDir { get; private set; }

    private readonly int _contextLength;
    private Process? _process;
    private StreamWriter? _logWriter;
    private readonly object _logLock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Raised for each line of stdout/stderr from the server (UI log).</summary>
    public event Action<string>? LogLine;

    public BackendManager(
        string host = "127.0.0.1",
        int port = 8000,
        string? executablePath = null,
        int contextLength = 32768)
    {
        Host = host;
        Port = port;
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? DefaultExecutablePath()
            : executablePath!;
        _contextLength = contextLength;
    }

    public static string DefaultExecutablePath()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(roaming, "uv", "tools", "openhands", "Scripts", "agent-server.exe");
    }

    public bool ExecutableExists => File.Exists(ExecutablePath);

    public bool IsRunning => _process is { HasExited: false };

    public async Task<bool> StartAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        AgentSpecProvider.ClearAvailableOptionalTools();

        // Write our Python helper modules and kick off the tool-registry probe in
        // parallel with server startup. The probe tells us which optional/alias
        // tools the engine actually has registered, so AgentSpecProvider only puts
        // confirmed tools in the agent spec (listing an unregistered tool makes the
        // server fail conversation creation). Best-effort: a null dir or a failed
        // probe simply means no aliases this session.
        var extDir = EngineExtensions.EnsureModuleDir();
        Task<HashSet<string>>? probeTask =
            extDir is null ? null : ProbeRegisteredToolsAsync(extDir, ct);

        if (await IsHealthyAsync(ct))
        {
            // A healthy port does not mean it belongs to this WPF process. After a
            // crash/rebuild/relaunch, an old agent-server can keep running with stale
            // conversations and stale prompts. Reusing it makes new code look broken
            // because the backend is still executing yesterday's instructions. Kill
            // any matching StayVibin agent-server first, then start a clean one.
            Log($"[backend] found existing server on {BaseUrl}; stopping stale server before fresh start");
            StopExistingServerProcesses();
            await WaitUntilUnhealthyAsync(TimeSpan.FromSeconds(8), ct);

            if (await IsHealthyAsync(ct))
            {
                Log($"[backend] existing server on {BaseUrl} could not be stopped");
                return false;
            }
        }

        if (!ExecutableExists)
            throw new FileNotFoundException(
                $"agent-server.exe not found at:\n{ExecutablePath}\n\n" +
                "Press Start to let StayVibin install it automatically, or install " +
                "the engine manually with: uv tool install openhands --python 3.12\n" +
                "Or set the path in Settings.", ExecutablePath);

        OpenLogFile();

        // Launch in the project folder when we have one so the engine's grep/glob
        // resolve relative paths (e.g. "src") against the project, not the install
        // dir. Fall back to the exe folder if no valid project dir was provided.
        var launchDir = (!string.IsNullOrWhiteSpace(WorkingDir) && Directory.Exists(WorkingDir))
            ? WorkingDir!
            : Path.GetDirectoryName(ExecutablePath) ?? Environment.CurrentDirectory;
        LaunchedWorkingDir = launchDir;

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = $"--host {Host} --port {Port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = launchDir
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["OLLAMA_CONTEXT_LENGTH"] = _contextLength.ToString();

        // Let small-context local models run. The engine otherwise refuses any model
        // whose context window is below 16,384 tokens (e.g. gemma2:9b at 8,192) and
        // fails the switch/start with a 500. This env var is the engine's own override
        // (read at LLM-validation time) so the model runs with its real window.
        psi.Environment["ALLOW_SHORT_CONTEXT_WINDOWS"] = "true";

        // Persist conversations to a stable, app-owned folder (absolute path) so chat
        // history survives restarts and is not written under the server's transient
        // launch directory. The server reloads every conversation here on startup.
        psi.Environment["OH_CONVERSATIONS_PATH"] = AppPaths.ConversationsDir;

        // Put our helper-module dir on the server's import path so it can import
        // sv_tool_aliases (requested via tool_module_qualnames) and register the
        // synonym tool aliases. Preserve any inherited PYTHONPATH.
        if (extDir is not null)
        {
            psi.Environment.TryGetValue("PYTHONPATH", out var existingPyPath);
            psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existingPyPath)
                ? extDir
                : extDir + Path.PathSeparator + existingPyPath;
        }

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };

        Log($"[backend] starting {ExecutablePath} --host {Host} --port {Port} (ctx={_contextLength})");
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                Log($"[backend] process exited early with code {_process.ExitCode}");
                return false;
            }
            if (await IsHealthyAsync(ct))
            {
                Log($"[backend] healthy at {BaseUrl}");
                if (probeTask is not null)
                {
                    // Publish the probed tool set before any conversation is created.
                    try { AgentSpecProvider.SetAvailableOptionalTools(await probeTask); }
                    catch { /* probe is best-effort; falls back to grep/glob only */ }
                }
                return true;
            }
            await Task.Delay(500, ct);
        }

        Log("[backend] timed out waiting for health check");
        return false;
    }

    /// <summary>
    /// Run sv_probe.py with the engine's own Python to learn which tools the engine
    /// has registered (after importing grep/glob/terminal and our alias module).
    /// Returns the set of registered tool names. Never throws: any failure yields an
    /// empty set, which makes AgentSpecProvider fall back to grep/glob only.
    /// </summary>
    private async Task<HashSet<string>> ProbeRegisteredToolsAsync(string extDir, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var scriptsDir = Path.GetDirectoryName(ExecutablePath);
            if (scriptsDir is null) return result;
            var python = Path.Combine(scriptsDir, "python.exe");
            if (!File.Exists(python)) return result;

            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"\"{Path.Combine(extDir, EngineExtensions.ProbeScript)}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = extDir
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["OPENHANDS_SUPPRESS_BANNER"] = "1";
            psi.Environment.TryGetValue("PYTHONPATH", out var existing);
            psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing)
                ? extDir
                : extDir + Path.PathSeparator + existing;

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            // Read both pipes concurrently so a full stderr buffer can't deadlock us.
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await errTask;
            await proc.WaitForExitAsync(ct);

            var marker = stdout.IndexOf("SVT:", StringComparison.Ordinal);
            if (marker >= 0)
            {
                var json = stdout[(marker + 4)..].Trim();
                var names = JsonSerializer.Deserialize<List<string>>(json);
                if (names is not null)
                    foreach (var n in names) result.Add(n);
            }
            Log($"[backend] tool probe found {result.Count} registered tools");
        }
        catch (Exception ex)
        {
            Log($"[backend] tool probe failed: {ex.Message}");
        }
        return result;
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

    private void StopExistingServerProcesses()
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

                Log($"[backend] killing stale {processName} pid={proc.Id}");
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log($"[backend] failed to kill stale server pid={proc.Id}: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/health", ct);
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
                Log("[backend] stopping server");
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

    private void OpenLogFile()
    {
        try
        {
            LogFilePath = AppPaths.NewServerLogPath();
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
