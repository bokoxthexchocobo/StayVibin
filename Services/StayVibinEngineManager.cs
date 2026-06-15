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
    private readonly ComputeDevice _device;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly object _logLock = new();
    private Process? _process;
    private StreamWriter? _logWriter;

    public event Action<string>? LogLine;

    public StayVibinEngineManager(
        string host = DefaultHost,
        int port = DefaultPort,
        string? executablePath = null,
        int contextLength = AppSettings.FallbackContextLength,
        ComputeDevice device = ComputeDevice.Gpu)
    {
        Host = host;
        Port = port;
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? DefaultExecutablePath()
            : executablePath!;
        _contextLength = contextLength;
        _device = device;
    }

    public static string DefaultExecutablePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Engine", "stayvibin-engine.exe");
        var bundledLib = Path.Combine(AppContext.BaseDirectory, "Engine", "lib", "ollama");

        // Dev fallback for local debug runs: when the WPF app is launched from the
        // repo, the build output may contain Engine\lib but not the copied
        // stayvibin-engine.exe yet. If the developer has the engine built in the
        // standard ollama-fork location, use that payload so app testing still works.
        var devRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "ollama-fork");
        var devExe = Path.Combine(devRoot, "stayvibin-engine.exe");
        var devLib = Path.Combine(devRoot, "build", "lib", "ollama");

        if (File.Exists(bundled) && RuntimePayloadLooksComplete(bundledLib))
            return bundled;

        if (File.Exists(devExe) && Directory.Exists(devLib) && Directory.Exists(bundledLib))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
                File.Copy(devExe, bundled, overwrite: true);
                StageDirectory(devLib, bundledLib);
                StageOptionalRuntimeDlls(bundledLib);
                return bundled;
            }
            catch
            {
                // Fall through to the missing-bundled path; StartAsync will log the failure.
            }
        }

        return bundled;
    }

    private static bool RuntimePayloadLooksComplete(string libDir)
    {
        return Directory.Exists(libDir)
            && File.Exists(Path.Combine(libDir, "ggml.dll"))
            && File.Exists(Path.Combine(libDir, "libllama.dll"))
            && File.Exists(Path.Combine(libDir, "llama-server.exe"))
            && File.Exists(Path.Combine(libDir, "libdl.dll"));
    }

    private static void StageDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void StageOptionalRuntimeDlls(string destDir)
    {
        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "mingw64", "bin", "libdl.dll"),
        ];

        foreach (var source in candidates)
        {
            if (!File.Exists(source)) continue;
            var dest = Path.Combine(destDir, Path.GetFileName(source));
            File.Copy(source, dest, overwrite: true);
        }
    }

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
            // Reuse only when BOTH the context length and the compute device match.
            // A device change (GPU <-> CPU) requires a relaunch because accelerator
            // visibility is fixed at process start via environment variables.
            if (RunningSignature() == (_contextLength, _device))
            {
                // Take ownership of the already-running engine so Stop()/Dispose()
                // (on app close) can actually kill it and its model runner. Without
                // this, a reused engine has no _process handle and survives shutdown,
                // leaving the model pinned in VRAM/RAM and the process "stuck" on.
                TryAdoptRunningEngine();
                return true;
            }
            var (runCtx, runDev) = RunningSignature();
            Log($"[engine] running engine signature != desired (ctx {runCtx}/{runDev} "
                + $"vs {_contextLength}/{_device}); restarting to apply context/device");
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
        var runtimeLibDir = Path.Combine(exeDir, "lib", "ollama");
        if (Directory.Exists(runtimeLibDir))
        {
            psi.Environment.TryGetValue("PATH", out var existingPath);
            psi.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
                ? runtimeLibDir
                : runtimeLibDir + Path.PathSeparator + existingPath;
        }
        // VRAM & performance tuning: we load the user's settings to apply custom
        // memory optimizations. Context memory (KV Cache) quantization (e.g. q8_0 or q4_0)
        // can save up to 50%-75% of context VRAM, allowing larger models or larger context sizes.
        var appSettings = AppSettings.Load();
        psi.Environment["OLLAMA_KV_CACHE_TYPE"] = !string.IsNullOrEmpty(appSettings.KvCacheType) ? appSettings.KvCacheType : "f16";
        psi.Environment["OLLAMA_FLASH_ATTENTION"] = appSettings.EnableFlashAttention ? "1" : "";
        psi.Environment["OLLAMA_MODELS"] = DefaultModelsDir();
        // Compute-device selection. CPU mode hides every accelerator from the engine
        // (CUDA/ROCm, and Vulkan which emulates CUDA_VISIBLE_DEVICES) so inference
        // runs purely on the CPU using system RAM. Slower, but it works on machines
        // with a weak or absent GPU. GPU mode leaves autodetection alone, which also
        // auto-spills layers that exceed VRAM onto the CPU.
        if (_device == ComputeDevice.Cpu)
        {
            psi.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
            psi.Environment["HIP_VISIBLE_DEVICES"] = "-1";
        }
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
        // Record the context length and compute device this engine was launched with
        // so a later StartAsync (e.g. after the user changes the context or device
        // setting, or on the next app run) can detect a mismatch and relaunch
        // instead of reusing a stale instance.
        WriteEngineSentinel(_contextLength, _device);

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

    private static void WriteEngineSentinel(int contextLength, ComputeDevice device)
    {
        try { File.WriteAllText(ContextSentinelPath, $"{contextLength}|{device}"); }
        catch { /* best-effort: a missing sentinel just forces a safe relaunch */ }
    }

    /// <summary>
    /// Context length and compute device the currently-running engine was launched
    /// with, read from the sentinel. Returns (-1, Gpu) when unknown (no sentinel /
    /// unreadable), which never equals a real signature so an engine of unknown
    /// provenance is relaunched.
    /// </summary>
    private static (int Ctx, ComputeDevice Device) RunningSignature()
    {
        try
        {
            if (File.Exists(ContextSentinelPath))
            {
                var parts = File.ReadAllText(ContextSentinelPath).Trim().Split('|');
                if (parts.Length >= 1 && int.TryParse(parts[0], out var v))
                {
                    var device = ComputeDevice.Gpu;
                    if (parts.Length >= 2)
                        Enum.TryParse(parts[1], out device);
                    return (v, device);
                }
            }
        }
        catch { /* fall through to unknown */ }
        return (-1, ComputeDevice.Gpu);
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

    /// <summary>
    /// Adopt an already-running engine process (matched by executable path) into
    /// <see cref="_process"/> so we can tear it down later. Used when StartAsync
    /// reuses a healthy engine that a previous app run launched. Best-effort: if the
    /// process cannot be located/queried, Stop() falls back to a name+path sweep.
    /// </summary>
    private void TryAdoptRunningEngine()
    {
        if (_process is { HasExited: false }) return;

        var expected = Path.GetFullPath(ExecutablePath);
        var processName = Path.GetFileNameWithoutExtension(ExecutablePath);
        // GetProcessesByName returns live OS handles; every entry we do NOT keep must
        // be disposed or we leak a process handle each adopt attempt. The finally
        // disposes all non-adopted entries regardless of which branch we exit on.
        var procs = Process.GetProcessesByName(processName);
        try
        {
            foreach (var proc in procs)
            {
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }
                if (string.IsNullOrWhiteSpace(path)
                    || !Path.GetFullPath(path).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    continue;

                _process = proc;
                Log($"[engine] adopted already-running engine pid={proc.Id}");
                return;
            }
        }
        finally
        {
            foreach (var proc in procs)
            {
                if (!ReferenceEquals(proc, _process))
                    try { proc.Dispose(); } catch { }
            }
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
                // A runner can outlive the tree kill in rare cases; sweep to be sure.
                StopOrphanedRunners();
            }
            else
            {
                // We never owned a handle (e.g. a reused engine that could not be
                // adopted). Sweep any engine process matching our exe so shutdown
                // still frees the model and never leaves an orphaned runner behind.
                StopExistingEngineProcesses();
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

        StopOrphanedRunners();
    }

    /// <summary>
    /// Kill any bundled llama-server runner that belongs to OUR engine (matched by
    /// path under the engine's lib\ollama dir) but has been orphaned - e.g. its
    /// parent engine died without an entire-process-tree kill. Such a runner keeps
    /// the model resident in VRAM/RAM, so sweeping it on start/stop frees memory and
    /// prevents "the engine keeps running" after the app is closed. Path-scoped so it
    /// never touches an unrelated llama.cpp install elsewhere on the machine.
    /// </summary>
    private void StopOrphanedRunners()
    {
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(ExecutablePath));
        if (string.IsNullOrEmpty(exeDir)) return;
        var runnerDir = Path.GetFullPath(Path.Combine(exeDir, "lib", "ollama"));

        foreach (var proc in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                string? path = null;
                try { path = proc.MainModule?.FileName; } catch { }
                if (string.IsNullOrWhiteSpace(path)
                    || !Path.GetFullPath(path).StartsWith(runnerDir, StringComparison.OrdinalIgnoreCase))
                {
                    proc.Dispose();
                    continue;
                }

                Log($"[engine] killing orphaned llama-server pid={proc.Id}");
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Log($"[engine] failed to kill orphaned runner pid={proc.Id}: {ex.Message}");
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
