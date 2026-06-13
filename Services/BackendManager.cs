using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace StayVibin.Services;

/// <summary>
/// Owns the OpenHands agent-server child process: locates the executable, starts
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
        if (await IsHealthyAsync(ct))
        {
            Log($"[backend] reusing server already listening on {BaseUrl}");
            return true;
        }

        if (!ExecutableExists)
            throw new FileNotFoundException(
                $"agent-server.exe not found at:\n{ExecutablePath}\n\n" +
                "Press Start to let StayVibin install it automatically, or install " +
                "OpenHands manually with: uv tool install openhands\n" +
                "Or set the path in Settings.", ExecutablePath);

        OpenLogFile();

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = $"--host {Host} --port {Port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? Environment.CurrentDirectory
        };

        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["OLLAMA_CONTEXT_LENGTH"] = _contextLength.ToString();

        // Let small-context local models run. OpenHands otherwise refuses any model
        // whose context window is below 16,384 tokens (e.g. gemma2:9b at 8,192) and
        // fails the switch/start with a 500. This env var is OpenHands' own override
        // (read at LLM-validation time) so the model runs with its real window.
        psi.Environment["ALLOW_SHORT_CONTEXT_WINDOWS"] = "true";

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
                return true;
            }
            await Task.Delay(500, ct);
        }

        Log("[backend] timed out waiting for health check");
        return false;
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
