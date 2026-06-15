using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Interactivity;
using AvaloniaEdit.Highlighting;
using StayVibin.Models;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// Main window and UI controller. Owns the backend process (via BackendManager),
/// the per-session conversation client (AgentServerClient), and the Ollama metadata
/// client. All server events arrive on a background thread and are marshalled onto
/// the UI thread before touching any control or the chat collection.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChatItem> _chat = new();
    private AppSettings _settings = AppSettings.Load();
    private BackendManager _backend;
    private StayVibinEngineManager? _engine;
    private AgentServerClient? _client;

    private string _workingDir;
    private bool _busy;             // a Start/Send operation is in flight
    private bool _agentRunning;     // the agent loop is actively running
    private ChatItem? _streamingItem;
    private readonly System.Text.StringBuilder _streamRaw = new();   // raw stream before tool-syntax stripping
    private bool _streamStripMode;  // once tool markup is seen, re-strip the whole stream each delta
    private bool _streamEnvelopeMode;  // once a JSON reply envelope is seen, show only its message
    private bool _turnUsedTools;    // true once the current turn invokes a tool or receives an observation
    private bool _turnAutoNudged;   // prevent infinite "continue and actually work" retries
    private string _lastAssistantText = "";

    // Counts consecutive tool-call schema validation failures (the model invents
    // tool parameters that the tool does not accept). After a couple in a row we
    // warn once per model that it is likely too weak to use the tools correctly.
    private int _toolSchemaErrors;
    private bool _weakModelWarned;

    // Plan Mode: true while the agent has presented a plan and is waiting for the
    // operator to approve before making changes. _lastAgentItem is the most recent
    // assistant bubble, so we can strip the PLAN_READY marker out of it in place.
    private bool _planAwaitingApproval;
    private ChatItem? _lastAgentItem;
    private bool _suppressPlanEvent;   // ignore the combo's SelectionChanged during init
    private bool _permissionAwaitingApproval;
    private bool _suppressPermissionEvent;

    // Messages typed while the agent is busy wait here and are sent one at a time
    // as each agent turn finishes (queueing is the default; Steer interjects now).
    private readonly Queue<QueuedMessage> _queue = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    /// <summary>A user message waiting to be sent after the current turn finishes.</summary>
    private sealed record QueuedMessage(ChatItem Bubble, string Text, List<string> Images);

    // Conversation history sidebar. Conversations are persisted by the agent-server;
    // these rows mirror that list. _activeConversationId is the one currently open.
    private readonly ObservableCollection<ConversationRow> _conversations = new();
    private readonly HashSet<string> _hiddenConversationIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeConversationId;
    private bool _suppressConvSelection;   // ignore ListBox selection we set in code
    private string? _pendingTitleConvId;   // refresh the list once this new chat is titled
    private bool _activeConvHadInput;      // did the user send anything in the active chat?

    private OllamaClient? _ollama;
    private JsonNode? _llmTemplate;     // detached clone of the configured LLM (for switches)
    private string? _selectedModel;     // ollama tag, e.g. "qwen2.5-coder:14b"
    private bool _populatingModels;
    private readonly SemaphoreSlim _modelLoadLock = new(1, 1);   // serialize PopulateModelsAsync
    private readonly HashSet<string> _modelAdviceShown = new(StringComparer.OrdinalIgnoreCase);
    private int _engineContext;

    // ---- Model Store Fields ----
    private readonly ObservableCollection<CatalogRow> _availableStoreModels = new();
    // Rows shown in the in-form "Installed Models" overlay.
    private readonly ObservableCollection<InstalledModelRow> _installedModels = new();
    // Guards the Installed Models overlay against overlapping refresh/remove actions.
    private bool _installedPanelBusy;
    private bool _storeBusy;
    private readonly CancellationTokenSource _storeCts = new();
    private CancellationTokenSource? _pullCts;

    // ---- Settings Fields ----
    private JsonNode? _settingsSpec;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chat;
        ConversationList.ItemsSource = _conversations;
        CatalogGrid.ItemsSource = _availableStoreModels;
        InstalledList.ItemsSource = _installedModels;

        _backend = BuildBackend();
        _engine = BuildEngineManager();
        _workingDir = _settings.EffectiveWorkingDir;
        WorkDirButton.Content = ShortPath(_workingDir);
        ToolTip.SetTip(WorkDirButton, _workingDir);

        if (AgentSpecProvider.SettingsExist)
        {
            // Permanently scrub legacy fields from the saved spec (subagent delegation
            // tool + cloud-only reasoning options) so a stale config can never spawn a
            // subagent that drives local models into the empty-response/stuck loop.
            AgentSpecProvider.SanitizeSavedSpec();
            try
            {
                var spec = AgentSpecProvider.Load();
                _llmTemplate = JsonNode.Parse(spec["llm"]!.ToJsonString());
                _selectedModel = StripProvider(AgentSpecProvider.DescribeModel(spec));
            }
            catch { /* model picker still works once configured */ }
        }
        _ollama = new OllamaClient(_settings.OllamaUrl);
        // Pre-session placeholder; refined per-model once tuned/started.
        _assumedContextWindow = _settings.BackendContextLength;

        // Reflect the saved Plan Mode without firing the change handler (which saves
        // and posts a chat note). Enum order matches the combo item order.
        _suppressPlanEvent = true;
        PlanModeCombo.SelectedIndex = (int)_settings.PlanMode;
        _suppressPlanEvent = false;
        _suppressPermissionEvent = true;
        PermissionModeCombo.SelectedIndex = (int)_settings.PermissionMode;
        _suppressPermissionEvent = false;

        AddSystem("Welcome. Pick a working folder, then press Start to launch the agent.");
        Closing += (_, _) => Cleanup();
        Loaded += OnWindowLoaded;

        // Explorer: populate children lazily on expand; double-click opens a file.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, OnTreeItemExpanded);
        CodeEditor.TextChanged += OnEditorTextChanged;
        CodeEditor.KeyDown += OnEditorKeyDown;

        _ = StartEngineAndPopulateModelsAsync();
        _ = UpdateRepoBadgeAsync();
        _ = RefreshTreeAsync();
    }

    private async Task StartEngineAndPopulateModelsAsync()
    {
        var engineOk = await EnsureOllamaRunningAsync();
        await PopulateModelsAsync();

        // EnsureOllamaRunningAsync leaves a "Starting StayVibin Engine..." status on
        // the bar; clear it once startup finished so the user sees the app is idle
        // and ready (no session is active yet at this point). On failure the error
        // path already set a Down status, so leave that in place.
        if (engineOk && !_agentRunning && _client is null)
            SetStatus("Ready", AppDot.Idle);
    }

    /// <summary>
    /// Surface a short, human-readable warning when the selected model is likely too
    /// weak for agentic tool use. Shown once per model per app run to avoid spam.
    /// </summary>
    private async Task ShowModelFitnessAdviceAsync(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !_modelAdviceShown.Add(model)) return;

        ModelInfo? info = _ollama is null ? null : await _ollama.GetModelInfoAsync(model);
        var notice = ModelAdvisor.Assess(model, info);
        if (notice is null)
        {
            // Freshly assessed model is fine - clear any stale suggestion bar.
            HideModelSuggestions();
            return;
        }

        AddSystem($"Model guidance ({notice.Severity}): {notice.Message}");
        ShowModelSuggestions(notice.Suggestions);
    }

    /// <summary>
    /// Show the in-chat suggestion bar with one-click Install buttons for the given
    /// models (plus Open Store / Dismiss). Buttons are built dynamically so the set
    /// of suggested models can vary by the selected model's size. Hidden when there
    /// is nothing to suggest.
    /// </summary>
    private void ShowModelSuggestions(IReadOnlyList<string> models)
    {
        ModelSuggestionButtons.Children.Clear();

        if (models is null || models.Count == 0)
        {
            ModelSuggestionBar.IsVisible = false;
            return;
        }

        foreach (var model in models)
        {
            var btn = new Button
            {
                Content = "Install " + model,
                Margin = new Thickness(0, 0, 8, 0),
                Tag = model,
            };
            btn.Classes.Add("accent");
            ToolTip.SetTip(btn, $"Download {model} in the Model Store (no commands needed)");
            btn.Click += OnInstallSuggestion;
            ModelSuggestionButtons.Children.Add(btn);
        }

        var store = new Button
        {
            Content = "Open Store",
            Margin = new Thickness(0, 0, 8, 0),
        };
        store.Classes.Add("flat");
        ToolTip.SetTip(store, "Browse and manage all models");
        store.Click += (_, _) =>
        {
            MainTabControl.SelectedIndex = 1; // Switch to Store Tab
            TabChatBtn.Classes.Remove("active");
            TabStoreBtn.Classes.Add("active");
        };
        ModelSuggestionButtons.Children.Add(store);

        var dismiss = new Button
        {
            Content = "Dismiss",
        };
        dismiss.Classes.Add("flat");
        ToolTip.SetTip(dismiss, "Hide this suggestion");
        dismiss.Click += (_, _) => HideModelSuggestions();
        ModelSuggestionButtons.Children.Add(dismiss);

        ModelSuggestionBar.IsVisible = true;
    }

    private void HideModelSuggestions()
    {
        ModelSuggestionBar.IsVisible = false;
        ModelSuggestionButtons.Children.Clear();
    }

    /// <summary>Install button in the suggestion bar: open the store and pull it.</summary>
    private async void OnInstallSuggestion(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model } || string.IsNullOrWhiteSpace(model))
            return;

        HideModelSuggestions();
        MainTabControl.SelectedIndex = 1; // Switch to Model Store Tab
        TabChatBtn.Classes.Remove("active");
        TabStoreBtn.Classes.Add("active");
        await InstallStoreAsync(model);
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;   // run once
        PromptInstallGitIfMissing();

        LoadSettingsToUi();
        _ = RefreshStoreAsync();

        // First run: no model configured yet - walk the user through provider setup
        // so they never have to hand-write agent_settings.json.
        if (!AgentSpecProvider.SettingsExist)
            await EnsureProviderConfiguredAsync();
    }

    /// <summary>
    /// Ensure a model/provider is configured (agent_settings.json exists), showing
    /// the first-run provider dialog and writing a default config when it isn't.
    /// Returns false if the user dismissed setup without configuring.
    /// </summary>
    private async Task<bool> EnsureProviderConfiguredAsync()
    {
        if (AgentSpecProvider.SettingsExist) return true;

        try
        {
            // Auto-configure the bundled StayVibin Engine as the default provider so
            // the app never depends on a separately-installed Ollama.
            AgentSpecProvider.CreateDefault("qwen2.5-coder:7b", StayVibinEngineManager.DefaultBaseUrl);

            // Keep the app's engine URL in sync.
            _settings.OllamaUrl = StayVibinEngineManager.DefaultBaseUrl;
            _settings.Save();
            _ollama?.Dispose();
            _ollama = new OllamaClient(_settings.OllamaUrl);

            var spec = AgentSpecProvider.Load();
            _llmTemplate = JsonNode.Parse(spec["llm"]!.ToJsonString());
            _selectedModel = StripProvider(AgentSpecProvider.DescribeModel(spec));
        }
        catch (Exception ex)
        {
            AddError($"Could not write the provider config: {ex.Message}");
            return false;
        }

        await MessageBox.ShowAsync(this, 
            "We've automatically configured StayVibin to use the bundled StayVibin Engine with a default model (qwen2.5-coder:7b).\n\n" +
            "Open the Models tab to download a model, then change the model, API key, base URL, and other parameters in the Settings tab at any time!",
            "Welcome to StayVibin!");

        await PopulateModelsAsync();
        AddSystem("Configured the StayVibin Engine with model qwen2.5-coder:7b. Open the Models tab to download it, then press Start.");
        return true;
    }

    /// <summary>
    /// Start (or verify) the bundled StayVibin Engine before a session. The
    /// agent-server has no model backend without an Ollama-compatible API, so we
    /// bring up our engine first instead of asking the user to run a separate app.
    /// </summary>
    private async Task<bool> EnsureOllamaRunningAsync()
    {
        if (_ollama is null) _ollama = new OllamaClient(_settings.OllamaUrl);

        if (StayVibinEngineManager.IsDefaultEngineUrl(_settings.OllamaUrl))
        {
            _engine ??= BuildEngineManager();
            SetStatus("Starting SV Engine...", AppDot.Connecting);
            if (!await _engine.StartAsync(TimeSpan.FromSeconds(60)))
            {
                AddError($"StayVibin Engine is not reachable at {_settings.OllamaUrl}.\n"
                         + $"Expected bundled engine at:\n{_engine.ExecutablePath}\n\n"
                         + "Rebuild or reinstall StayVibin so the Engine folder is present.");
                return false;
            }
        }
        else
        {
            SetStatus("Checking model engine...", AppDot.Connecting);
        }

        if (await _ollama.IsReachableAsync()) return true;

        AddError($"The model engine is not reachable at {_settings.OllamaUrl}.\n"
                 + "If you configured a custom engine URL, start that server and press Start "
                 + "again. Otherwise reinstall StayVibin so the bundled engine is available.");
        return false;
    }

    private int _backendContext;

    private BackendManager BuildBackend()
    {
        _backendContext = _settings.ContextLength;   // raw setting (0 = auto) for change detection
        var b = new BackendManager(
            _settings.Host, _settings.Port,
            _settings.EffectiveAgentServerPath, _settings.BackendContextLength);
        b.LogLine += OnBackendLog;
        return b;
    }

    private StayVibinEngineManager BuildEngineManager(int? contextLength = null)
    {
        _engineContext = contextLength ?? _settings.BackendContextLength;
        var e = new StayVibinEngineManager(contextLength: _engineContext, device: _settings.ComputeDevice);
        // Mirror the engine's stdout/stderr into the in-app Server log (and the log
        // file) the same way the backend does, so engine startup/CUDA failures are
        // visible to the user instead of only landing in the engine-*.log file.
        e.LogLine += OnBackendLog;
        return e;
    }

    private void RebuildEngineManager(int? contextLength = null)
    {
        try { _engine?.Dispose(); } catch { }
        _engine = BuildEngineManager(contextLength);
    }

    /// <summary>Tear down the old backend (unsubscribe log handler) and spawn a fresh one.</summary>
    private void RebuildBackend()
    {
        _backend.LogLine -= OnBackendLog;
        try { _backend.Dispose(); } catch { }
        _backend = BuildBackend();
    }

    /// <summary>
    /// Make sure the running server was launched in the current <see cref="_workingDir"/>.
    /// The engine resolves relative grep/glob paths against the server's process
    /// directory, so a mismatch (e.g. reopening a chat from another project) would
    /// make relative searches miss. Relaunches the backend in place when needed.
    /// </summary>
    private async Task EnsureBackendInWorkingDirAsync()
    {
        if (_backend.IsRunning && PathsEqual(_backend.LaunchedWorkingDir, _workingDir))
            return;
        RebuildBackend();
        _backend.WorkingDir = _workingDir;
        await _backend.StartAsync(TimeSpan.FromSeconds(60));
    }

    /// <summary>Case-insensitive comparison of two filesystem paths (Windows).</summary>
    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string p) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }


    /// <summary>
    /// Fill the model dropdown from Ollama's installed list and refresh the capability
    /// strip. The installed list is authoritative: a configured model that is no longer
    /// installed is not shown (the selection falls back to an installed model). The
    /// configured model is only used as a fallback when Ollama returns nothing
    /// (offline). Embedding-only models are listed but disabled (they can't chat).
    /// </summary>
    private async Task PopulateModelsAsync()
    {
        // Serialize so overlapping callers (startup + first-run, rapid Refresh, or a
        // settings save) can't interleave their dropdown updates or clear the
        // _populatingModels guard out from under each other.
        await _modelLoadLock.WaitAsync();
        try
        {
            await PopulateModelsCoreAsync();
        }
        finally
        {
            _modelLoadLock.Release();
        }
    }

    private async Task PopulateModelsCoreAsync()
    {
        // Capture once so a concurrent settings change (which disposes/recreates
        // _ollama) can't swap the client mid-scan.
        var ollama = _ollama;
        var models = ollama is null
            ? Array.Empty<string>()
            : await ollama.ListModelsAsync();

        // Look up capabilities (cached) so we can grey out embedding-only models.
        var infos = new Dictionary<string, ModelInfo?>(StringComparer.OrdinalIgnoreCase);
        if (ollama is not null && models.Count > 0)
        {
            var pairs = await Task.WhenAll(
                models.Select(async m => (m, info: await ollama.GetModelInfoAsync(m))));
            foreach (var (m, info) in pairs) infos[m] = info;
        }

        _populatingModels = true;
        try
        {
            ModelCombo.Items.Clear();

            // The installed list is authoritative when Ollama answered. Only fall back
            // to the configured model when Ollama returned nothing (offline) so the
            // dropdown still shows something - never inject a model that Ollama says is
            // not installed (e.g. one the user just deleted), or it shows phantoms.
            var names = new List<string>(models);
            if (names.Count == 0 && _selectedModel is not null)
                names.Insert(0, _selectedModel);

            ModelEntry? toSelect = null;
            foreach (var name in names)
            {
                var entry = new ModelEntry
                {
                    Name = name,
                    IsEmbedding = IsEmbeddingOnly(infos.GetValueOrDefault(name))
                };
                ModelCombo.Items.Add(entry);
                if (_selectedModel is not null &&
                    name.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase))
                    toSelect = entry;
            }

            // Prefer the configured model; otherwise the first model that can chat.
            toSelect ??= ModelCombo.Items.Cast<ModelEntry>().FirstOrDefault(e => e.IsSelectable);
            ModelCombo.SelectedItem = toSelect;

            // Keep _selectedModel pointing at a model that actually exists, so the rest
            // of the app (Start, capability strip, warm-up) never targets a deleted one.
            if (toSelect is not null) _selectedModel = toSelect.Name;

            if (names.Count == 0)
                AddSystem("No Ollama models detected - is Ollama running on this machine?");

            _ = UpdateCapabilitiesAsync((ModelCombo.SelectedItem as ModelEntry)?.Name ?? _selectedModel);
        }
        finally
        {
            _populatingModels = false;
        }
    }

    /// <summary>
    /// Re-scan Ollama for installed models so a newly pulled model shows up without
    /// restarting the app. The model list itself is never cached, so a refresh picks
    /// up additions immediately.
    /// </summary>
    private async void OnRefreshModels(object sender, RoutedEventArgs e)
    {
        if (_ollama is null) return;
        RefreshModelsButton.IsEnabled = false;
        try
        {
            // Drop cached metadata so capabilities/context re-fetch for every model
            // (covers transient failures and re-pulled/retagged models).
            _ollama.ClearCache();
            await PopulateModelsAsync();
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }


    /// <summary>True for models that only do embeddings (no chat/completion).</summary>
    private static bool IsEmbeddingOnly(ModelInfo? info)
    {
        var caps = info?.Capabilities;
        if (caps is null || caps.Count == 0) return false;
        bool embed = caps.Any(c => c.Equals("embedding", StringComparison.OrdinalIgnoreCase));
        bool chat = caps.Any(c => c.Equals("completion", StringComparison.OrdinalIgnoreCase));
        return embed && !chat;
    }

    // ---- model capability strip --------------------------------------------

    private static readonly Dictionary<string, (string Icon, string Label, string Tip)> CapMeta =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["completion"] = ("\U0001F4AC", "Chat",
                "Text & chat generation - the model can hold a conversation and write text or code."),
            ["tools"] = ("\U0001F527", "Tools",
                "Tool use - the model can call the terminal, file editor and other tools to act on your machine. Needed for agent work."),
            ["insert"] = ("\U0001F4DD", "Insert",
                "Fill-in-the-middle - can insert or complete code inside an existing file using the surrounding context."),
            ["vision"] = ("\U0001F441", "Vision",
                "Vision - the model can read and reason about images you provide."),
            ["thinking"] = ("\U0001F9E0", "Thinking",
                "Reasoning - the model produces an internal chain of thought before answering."),
            ["embedding"] = ("\U0001F4D0", "Embed",
                "Embeddings - turns text into vectors for search/similarity. Not used for chat."),
        };

    private ModelInfo? _currentModelInfo;
    private int _capSeq;   // guards against out-of-order capability lookups

    // Models we have already warned about for broken tool calling, so the chat
    // warning fires once per model rather than on every re-selection.
    private readonly HashSet<string> _toolWarned = new(StringComparer.OrdinalIgnoreCase);

    private bool ModelHasVision =>
        _currentModelInfo?.Capabilities?.Any(c => c.Equals("vision", StringComparison.OrdinalIgnoreCase)) ?? false;

    private async Task UpdateCapabilitiesAsync(string? model)
    {
        // Each call gets a sequence number; if a newer call starts before this
        // one's network lookup returns, we drop the stale result so the strip and
        // vision flag always reflect the most recently selected model.
        var seq = ++_capSeq;

        if (string.IsNullOrWhiteSpace(model) || _ollama is null)
        {
            _currentModelInfo = null;
            RenderCapabilities(null);
            return;
        }

        var info = await _ollama.GetModelInfoAsync(model);
        if (seq != _capSeq) return;  // superseded by a newer selection

        _currentModelInfo = info;
        RenderCapabilities(info);

        // Then verify the model can actually emit structured tool calls. Some models
        // advertise a "tools" capability but return the call as plain text, which
        // makes the agent silently do nothing - probe in the background and warn.
        _ = VerifyToolCallingAsync(model, seq);
    }

    /// <summary>
    /// Background check: if the model returns tool calls as text instead of real
    /// tool_calls, append a warning badge to the capability strip and (once per
    /// model) post a chat message pointing the user at a model that works.
    /// </summary>
    private async Task VerifyToolCallingAsync(string model, int seq)
    {
        if (_ollama is null) return;

        var ok = await _ollama.ProbeToolCallingAsync(model);
        if (seq != _capSeq) return;           // model changed while probing
        if (ok != false) return;              // true (good) or null (unknown) -> stay quiet

        CapabilityPanel.Children.Add(MakeCapBadge("\u26A0", "no tool calls",
            $"{model} does not return structured tool calls - it writes them as plain "
            + "text, so the agent cannot act and appears to do nothing. Pick a model "
            + "built for tools (e.g. gpt-oss, qwen3, qwen3.5) for agentic work.",
            warn: true));

        if (_toolWarned.Add(model))
            AddError($"Heads up: '{model}' can't use tools properly on Ollama - it "
                     + "returns tool calls as plain text, so the agent will appear to "
                     + "stall or print raw JSON instead of running commands. Switch the "
                     + "model dropdown to a tool-capable model such as gpt-oss:20b, "
                     + "qwen3.5, or qwen3:14b for real agentic work.");
    }

    private void RenderCapabilities(ModelInfo? info)
    {
        CapabilityPanel.Children.Clear();

        if (info is null)
        {
            CapabilityPanel.Children.Add(MakeCapBadge("-", "unknown",
                "No model info - the model was not detected or Ollama is offline.", dim: true));
            return;
        }

        // Detected model details (family - size - native context default).
        var ctx = info.ContextLength >= 1024 ? $"{info.ContextLength / 1024}K native"
                : info.ContextLength > 0 ? $"{info.ContextLength} native" : "";
        var parts = new[] { info.Family, info.ParameterSize, ctx }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var label = string.Join(" - ", parts);
        if (!string.IsNullOrWhiteSpace(label))
            CapabilityPanel.Children.Add(MakeCapBadge("\U0001F9E9", label,
                $"Detected model: family {info.Family}, size {info.ParameterSize}, "
                + $"Ollama default context {info.ContextLength:N0} tokens. "
                + $"Runtime context uses your Settings cap ({_settings.ContextLength:N0})."));

        var caps = info.Capabilities;
        if (caps is null || caps.Count == 0) return;

        foreach (var cap in caps)
        {
            if (CapMeta.TryGetValue(cap, out var meta))
                CapabilityPanel.Children.Add(MakeCapBadge(meta.Icon, meta.Label, meta.Tip));
            else
                CapabilityPanel.Children.Add(MakeCapBadge("\u2699", cap, $"Model capability: {cap}"));
        }
    }

    /// <summary>Build one rounded icon+label badge with an explanatory tooltip.</summary>
    private Border MakeCapBadge(string icon, string label, string tip, bool dim = false, bool warn = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Margin = new Thickness(5, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (IBrush)this.FindResource(warn ? "Warn" : dim ? "TextDim" : "Text")!
        });

        var badge = new Border
        {
            Background = (IBrush)this.FindResource("PanelAlt")!,
            BorderBrush = (IBrush)this.FindResource(warn ? "Warn" : "Border")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = row
        };
        ToolTip.SetTip(badge, tip);
        return badge;
    }

    // ---- UI event handlers --------------------------------------------------

    /// <summary>Let the user choose the folder the agent will operate in.</summary>
    private async void OnPickWorkDir(object sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Choose working directory",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri(_workingDir))
        });
        if (folders.Count > 0)
        {
            _workingDir = folders[0].Path.LocalPath;
            WorkDirButton.Content = ShortPath(_workingDir);
            ToolTip.SetTip(WorkDirButton, _workingDir);
            if (_client is null)
                await EnsureBackendInWorkingDirAsync();
            await UpdateRepoBadgeAsync();
            await RefreshTreeAsync();
        }
    }

    // ---- git / GitHub -------------------------------------------------------

    private const string GitDownloadUrl = "https://git-scm.com/download/win";

    /// <summary>
    /// If Git isn't installed, offer to send the user to the official download page.
    /// Git underpins the agent's version-control and GitHub features.
    /// </summary>
    private async void PromptInstallGitIfMissing()
    {
        if (GitService.GitAvailable) return;

        var choice = await MessageBox.ShowAsync(
            this,
            "Git is not installed on this PC.\n\n"
            + "StayVibin uses Git for version control and GitHub features "
            + "(commits, branches, pull requests). Would you like to download and "
            + "install it now?\n\nAfter installing, restart StayVibin.",
            "Install Git?",
            MessageBoxButton.YesNo);

        if (choice == MessageBoxResult.Yes)
            OpenUrl(GitDownloadUrl);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* no default browser / blocked - nothing we can do */ }
    }

    private void OnRepoRefresh(object sender, RoutedEventArgs e) => _ = UpdateRepoBadgeAsync();

    /// <summary>Refresh the top-bar git badge for the current working directory.</summary>
    private async Task UpdateRepoBadgeAsync()
    {
        if (!GitService.GitAvailable)
        {
            RepoButton.IsVisible = false;
            return;
        }

        var status = await GitService.GetStatusAsync(_workingDir);
        if (status is null)
        {
            RepoButton.IsVisible = false;
            return;
        }

        // Sidebar icon button: show a branch glyph, tint it amber when the tree is
        // dirty, and put the full branch/status detail in the tooltip.
        RepoButton.Content = "\u2387";
        RepoButton.Foreground = status.IsDirty ? (IBrush)this.FindResource("Warn")! : (IBrush)this.FindResource("TextDim")!;

        var dirtyMark = status.IsDirty ? " *" : "";
        var tip = (status.RepoSlug is null ? "" : $"{status.RepoSlug}\n")
            + $"git: {status.Branch}{dirtyMark}\n"
            + (status.IsDirty ? $"{status.Dirty} uncommitted change(s)" : "Working tree clean")
            + "\n(click to refresh)";
        ToolTip.SetTip(RepoButton, tip);
        RepoButton.IsVisible = true;
    }

    /// <summary>Log what git/GitHub tooling the agent has, once per session start.</summary>
    private async Task ReportGitToolingAsync()
    {
        if (!GitService.GitAvailable)
        {
            AddSystem("git was not found on PATH - install Git to let the agent use version control.");
            return;
        }

        var account = await GitService.GhAccountAsync();
        if (GitService.GhAvailable && account is not null)
            AddSystem($"git + GitHub CLI ready (gh signed in as {account}). The agent can clone, commit, branch, and open PRs.");
        else if (GitService.GhAvailable)
            AddSystem("git ready; GitHub CLI (gh) is installed but not signed in - run 'gh auth login' to enable GitHub actions.");
        else
            AddSystem("git ready. Install the GitHub CLI (gh) for PRs/issues/releases.");
    }

    /// <summary>
    /// Make sure agent-server.exe exists, offering a one-click automatic install
    /// (via uv) when it doesn't. Returns true when the backend is ready to launch.
    /// </summary>
    private async Task<bool> EnsureBackendInstalledAsync()
    {
        if (_backend.ExecutableExists) return true;

        // If the user pointed us at an explicit path, don't try to install over it -
        // just tell them it's wrong so they can fix the path or clear it.
        if (!string.IsNullOrWhiteSpace(_settings.AgentServerPath))
        {
            AddError("agent-server.exe was not found at your configured path:\n"
                     + $"{_settings.EffectiveAgentServerPath}\n"
                     + "Fix or clear the path in Settings (clearing it lets StayVibin "
                     + "install the AI engine automatically).");
            return false;
        }

        var choice = await MessageBox.ShowAsync(
            this,
            "StayVibin's AI engine isn't installed yet.\n\n"
            + "StayVibin can set it up for you automatically (it installs uv if needed, "
            + "then downloads the engine). This needs an internet connection and can "
            + "take a few minutes.\n\nInstall it now?",
            "Set up StayVibin's AI engine?",
            MessageBoxButton.YesNo);
        if (choice != MessageBoxResult.Yes)
        {
            AddSystem("The AI engine is not installed. You can install it later by "
                      + "pressing Start again.");
            return false;
        }

        SetStatus("Installing AI engine...", AppDot.Connecting);
        SetActivity(true);   // show the activity bar during the long install
        AddSystem("Setting up StayVibin's AI engine (one-time install)...");

        var installer = new StayVibinEngineInstaller();
        installer.LogLine += OnBackendLog;   // mirror install output into the server log
        try
        {
            bool ok = await installer.EnsureInstalledAsync();
            if (!ok)
            {
                AddError("Automatic setup of the AI engine failed - see the Server log "
                         + "for details. You can also install it manually by running: "
                         + "uv tool install openhands --python 3.12");
                return false;
            }

            RebuildBackend();   // rebind to the freshly installed agent-server.exe
            if (!_backend.ExecutableExists)
            {
                AddError("The AI engine installed but agent-server.exe still wasn't found "
                         + $"at {_backend.ExecutablePath}. Set the path in Settings.");
                return false;
            }

            AddSystem("AI engine installed successfully.");
            return true;
        }
        finally
        {
            installer.LogLine -= OnBackendLog;
            SetActivity(false);
        }
    }

    /// <summary>
    /// Top-right button handler. Acts as Start when idle (launch server, create
    /// conversation, connect the event socket) and Stop when a session is live.
    /// </summary>
    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // The top button toggles between Start and Stop.
        if (_client is not null)
        {
            await StopSessionAsync();
            return;
        }

        SetBusy(true);
        StartButton.IsEnabled = false;
        SetStatus("Starting server...", AppDot.Connecting);
        try
        {
            // First run convenience: configure a provider/model, then install the
            // agent-server automatically (via uv) - no terminal needed.
            if (!await EnsureProviderConfiguredAsync())
            {
                SetStatus("Not configured", AppDot.Down);
                SetSessionActive(false);
                return;
            }

            // Ollama must be up before we launch anything: the agent-server is useless
            // without a model backend, and starting it would only fail later.
            if (!await EnsureOllamaRunningAsync())
            {
                SetStatus("Ollama not running", AppDot.Down);
                SetSessionActive(false);
                return;
            }

            if (!await EnsureBackendInstalledAsync())
            {
                SetStatus("AI engine not installed", AppDot.Down);
                SetSessionActive(false);
                return;
            }

            JsonNode spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode, autoCompact: _settings.AutoCompact);
            _llmTemplate ??= JsonNode.Parse(spec["llm"]!.ToJsonString());
            if (_selectedModel is not null) ApplyModel(spec, _selectedModel);

            if (_settings.AutoTune)
                await AutoTuneSpecAsync(spec);
            else
            {
                // AutoTune off still needs correct tool-calling mode, otherwise the
                // saved spec's native_tool_calling=false breaks tool-capable models.
                await ApplyNativeToolCallingAsync(spec);
                // ...and it must still size the engine + token budget to the user's
                // context setting. Without this a manual "Max context" entry never
                // reaches the engine (it keeps the launch default) - the
                // "doesn't take manual entries" symptom when AutoTune is off.
                await ApplyRuntimeContextAsync(spec, _settings.BackendContextLength);
            }
            if (_selectedModel is not null)
                _ = ShowModelFitnessAdviceAsync(_selectedModel);

            // Respawn the server if the desired context size differs from what the
            // current backend was built with. OLLAMA_CONTEXT_LENGTH is applied at
            // process launch, so a reused (still-running) server must be torn down
            // for the new value to take effect. We're starting fresh here (_client
            // is null), so disposing any prior server is safe.
            if (_backendContext != _settings.ContextLength)
                RebuildBackend();

            // Launch the server inside the project folder so the engine's grep/glob
            // resolve relative paths (e.g. "src") against the project, not the
            // install dir. Without this every relative-path search the model tries
            // fails and it burns the whole context window flailing.
            _backend.WorkingDir = _workingDir;

            bool healthy = await _backend.StartAsync(TimeSpan.FromSeconds(60));
            if (!healthy)
            {
                SetStatus("Server failed to start (see log)", AppDot.Down);
                AddError("The agent-server did not become healthy. Open the Server log for details.");
                SetSessionActive(false);
                return;
            }

            await ConnectConversationAsync(spec);
            InputBox.Focus();
            AddSystem($"Connected. Working in {_workingDir}");
            AddSystem(DescribeAutoCompact(spec));
            await ReportGitToolingAsync();
            await UpdateRepoBadgeAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Error", AppDot.Down);
            AddError(ex.Message);
            await DisposeClientAsync();
            SetSessionActive(false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Create a fresh conversation from the given spec, wire up the event client and
    /// open the socket. Shared by Start and by live model switches (which recreate the
    /// conversation, since the server's switch_llm cannot reliably change a running
    /// conversation's model - see <see cref="RestartConversationWithModelAsync"/>).
    /// </summary>
    private async Task ConnectConversationAsync(JsonNode spec)
    {
        // Before creating a new conversation, drop the previous one if it was never
        // used, so unused Start/New/model-switch attempts don't leave empty chats.
        await DiscardActiveIfEmptyAsync();

        SetStatus("Creating conversation...", AppDot.Connecting);
        _client = new AgentServerClient(_backend.BaseUrl);
        SubscribeClient(_client);

        // Finalize the tool list for this conversation: add the optional/alias tools
        // the backend actually registered (grep/glob plus synonyms like search ->
        // grep, execute_powershell -> terminal) and strip any it does not have. The
        // returned map tells the server which module to import to register each.
        var toolModules = AgentSpecProvider.PrepareConversationTools(spec);
        await _client.StartConversationAsync(spec, _workingDir, _settings.MaxIterations, toolModules);
        await _client.SetConfirmationPolicyAsync(ToAgentPermissionPolicy(_settings.PermissionMode));
        await _client.ConnectAsync();

        _activeConversationId = _client.ConversationId;
        _pendingTitleConvId = _client.ConversationId;   // pick up its title after turn 1
        _activeConvHadInput = false;                    // fresh, empty conversation
        SetSessionActive(true);
        RefreshStatsDisplay();   // show configured runtime window before stats arrive
        _ = RefreshConversationListAsync();

        // Pre-load the model in the background so the user's first message isn't stuck
        // behind a cold load. Warming at the resolved runtime context avoids a later
        // reload when the agent's first request uses that same num_ctx.
        _ = WarmSelectedModelAsync();
    }

    /// <summary>Wire the per-conversation client events to the UI handlers.</summary>
    private void SubscribeClient(AgentServerClient c)
    {
        c.Update += OnAgentUpdate;
        c.StatusChanged += OnServerStatus;
        c.StatsUpdated += OnStats;
        c.CompactingStarted += OnCompactingStarted;
        c.Compacted += OnCompacted;
        c.Disconnected += OnDisconnected;
    }

    // ---- conversation history (persisted chats) -----------------------------

    /// <summary>Show/hide the conversation-history rail (mirrors the Files toggle).</summary>
    private void OnToggleHistory(object sender, RoutedEventArgs e)
    {
        HistoryPanel.IsVisible = !HistoryPanel.IsVisible;
        HistorySplitter.IsVisible = HistoryPanel.IsVisible;
    }

    /// <summary>
    /// Reload the conversation list from the server (most recent first) and
    /// re-highlight the active one. Best-effort; needs a running backend.
    /// </summary>
    private async Task RefreshConversationListAsync()
    {
        if (!_backend.IsRunning && !await _backend.IsHealthyAsync()) return;

        var list = await AgentServerClient.ListConversationsAsync(_backend.BaseUrl);

        _suppressConvSelection = true;
        _conversations.Clear();
        foreach (var c in list)
        {
            if (_hiddenConversationIds.Contains(c.Id)) continue;
            // Hide unused (empty) conversations, but always keep the active one so a
            // freshly created chat stays visible/highlighted until its first message.
            if (!c.HasUserMessage && c.Id != _activeConversationId) continue;
            _conversations.Add(new ConversationRow
            {
                Id = c.Id,
                Title = c.Title,
                WorkingDir = c.WorkingDir,
                UpdatedAt = c.UpdatedAt
            });
        }
        _suppressConvSelection = false;

        // Best-effort: prune abandoned empty conversations (no user message and not the
        // active one) so they don't accumulate on disk across sessions. Fire-and-forget;
        // they are already hidden above, this just reclaims the space.
        foreach (var c in list)
            if (!c.HasUserMessage && c.Id != _activeConversationId)
                _ = AgentServerClient.DeleteConversationAsync(_backend.BaseUrl, c.Id);

        HighlightActiveConversation();
        NewChatButton.IsEnabled = true;
    }

    /// <summary>Select the row for the active conversation without triggering reopen.</summary>
    private void HighlightActiveConversation()
    {
        _suppressConvSelection = true;
        ConversationList.SelectedItem =
            _conversations.FirstOrDefault(r => r.Id == _activeConversationId);
        _suppressConvSelection = false;
    }

    /// <summary>Start a brand-new conversation (same as Start, but always fresh).</summary>
    private async void OnNewChat(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        // We need a running server. Common case: it is already up - this branch has no
        // await before SetBusy, so two fast clicks can't both slip through.
        bool serverUp = _backend.IsRunning || await _backend.IsHealthyAsync();
        if (!serverUp)
        {
            // No server yet: defer to the normal Start flow, which launches it and
            // creates a fresh conversation (_client is null, so Start won't toggle off).
            OnStartClick(sender, e);
            return;
        }

        if (_busy) return;   // re-check: an await above may have let another click in
        SetBusy(true);
        StartButton.IsEnabled = false;
        try
        {
            await DisposeClientAsync();
            ResetChatView();

            var spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode, autoCompact: _settings.AutoCompact);
            if (_selectedModel is not null) ApplyModel(spec, _selectedModel);
            if (_settings.AutoTune) await AutoTuneSpecAsync(spec);
            else await ApplyNativeToolCallingAsync(spec);

            await EnsureBackendInWorkingDirAsync();
            await ConnectConversationAsync(spec);
            AddSystem($"New conversation. Working in {_workingDir}");
        }
        catch (Exception ex)
        {
            SetStatus("Error", AppDot.Down);
            AddError($"Could not start a new conversation: {ex.Message}");
            await DisposeClientAsync();
            SetSessionActive(false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Reopen a persisted conversation: replay its history then go live.</summary>
    private async void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConvSelection) return;
        if (ConversationList.SelectedItem is not ConversationRow row) return;
        // Re-selecting the live conversation is a no-op; but after Stop (_client null)
        // clicking it should reopen.
        if (row.Id == _activeConversationId && _client is not null) return;

        if (_busy)
        {
            HighlightActiveConversation();   // revert selection; can't switch mid-op
            return;
        }
        await OpenConversationAsync(row);
    }

    private async Task OpenConversationAsync(ConversationRow row)
    {
        SetBusy(true);
        StartButton.IsEnabled = false;
        SetStatus("Opening conversation...", AppDot.Connecting);
        try
        {
            await DisposeClientAsync();
            ResetChatView();

            // Restore the conversation's working directory so the explorer and any new
            // messages operate where this chat left off.
            if (!string.IsNullOrWhiteSpace(row.WorkingDir) && Directory.Exists(row.WorkingDir))
            {
                _workingDir = row.WorkingDir;
                WorkDirButton.Content = ShortPath(_workingDir);
                ToolTip.SetTip(WorkDirButton, _workingDir);
                _ = RefreshTreeAsync();
                _ = UpdateRepoBadgeAsync();
            }

            // The engine resolves relative grep/glob paths against the server's
            // launch directory. If this chat's project differs from where the server
            // is currently running, relaunch it there so relative searches work.
            await EnsureBackendInWorkingDirAsync();

            _client = new AgentServerClient(_backend.BaseUrl);
            SubscribeClient(_client);
            _client.AttachConversation(row.Id);

            // Rebuild the transcript from persisted events, THEN open the socket (which
            // streams only new events), so nothing is rendered twice.
            await _client.ReplayHistoryAsync();
            await _client.SetConfirmationPolicyAsync(ToAgentPermissionPolicy(_settings.PermissionMode));
            await _client.ConnectAsync();

            _activeConversationId = row.Id;
            _pendingTitleConvId = null;
            _activeConvHadInput = true;   // reopened chats already have content; keep them
            SetSessionActive(true);
            RefreshStatsDisplay();
            HighlightActiveConversation();
            AddSystem($"Reopened conversation. Working in {_workingDir}");
            _ = WarmSelectedModelAsync();
        }
        catch (AgentServerException ex) when (ex.IsNotFound)
        {
            HideConversationRow(row.Id);
            SetStatus("Ready", AppDot.Idle);
            AddSystem("That conversation is no longer available on the running agent-server, so it was removed from the list. Start a new chat to continue.");
            await DisposeClientAsync();
            SetSessionActive(false);
        }
        catch (Exception ex)
        {
            SetStatus("Error", AppDot.Down);
            AddError($"Could not open conversation: {ex.Message}");
            await DisposeClientAsync();
            SetSessionActive(false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Delete a persisted conversation (with confirmation).</summary>
    private async void OnDeleteConversation(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id || string.IsNullOrEmpty(id))
            return;

        var confirm = await MessageBox.ShowAsync(this,
            "Delete this conversation permanently? This cannot be undone.",
            "Delete conversation", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        // If this is the active chat, stop the event stream first. A running/stuck
        // conversation can reject DELETE until its socket/runtime is torn down.
        if (id == _activeConversationId && _client is not null)
        {
            await _client.InterruptAsync();
            await DisposeClientAsync();
        }

        var result = await AgentServerClient.DeleteConversationDetailedAsync(_backend.BaseUrl, id);
        if (!result.Succeeded)
        {
            HideConversationRow(id);
            if (id == _activeConversationId)
            {
                ResetChatView();
                _activeConversationId = null;
                SetSessionActive(false);
                SetStatus("Stopped", AppDot.Down);
            }

            AddSystem(result.IsNotFound
                ? "That conversation was already gone on the server, so it was removed from the list."
                : "The server rejected deleting that conversation, so it was hidden locally. Restarting the agent-server will clear any stuck runtime.");
            return;
        }

        // If we deleted the active conversation, tear down the live session too.
        if (id == _activeConversationId)
        {
            await DisposeClientAsync();
            ResetChatView();
            _activeConversationId = null;
            SetSessionActive(false);
            SetStatus("Stopped", AppDot.Down);
            AddSystem("Conversation deleted.");
        }
        await RefreshConversationListAsync();
    }

    /// <summary>Hide a stale or stuck conversation row without deleting persisted files.</summary>
    private void HideConversationRow(string id)
    {
        _hiddenConversationIds.Add(id);
        var row = _conversations.FirstOrDefault(c => c.Id == id);
        if (row is not null)
            _conversations.Remove(row);
        HighlightActiveConversation();
    }

    /// <summary>
    /// Delete the active conversation if the user never sent a message in it. A new
    /// conversation is persisted the moment it is created, so without this every
    /// Start/New/model-switch that goes unused would leave an empty "New chat" behind
    /// (cluttering the list and growing the conversations folder). Safe because an
    /// input-free conversation has no agent activity to lose. Best-effort.
    /// </summary>
    private async Task DiscardActiveIfEmptyAsync()
    {
        var id = _activeConversationId;
        if (id is null || _activeConvHadInput) return;
        _activeConversationId = null;
        await AgentServerClient.DeleteConversationAsync(_backend.BaseUrl, id);
    }

    /// <summary>Clear the chat view and per-stream state for a fresh/reopened session.</summary>
    private void ResetChatView()
    {
        _chat.Clear();
        _activeThought = null;
        _streamingItem = null;
        _lastAgentItem = null;
        _streamRaw.Clear();
        _streamStripMode = false;
        _streamEnvelopeMode = false;
        _lastAssistantText = "";
        // Fresh conversation: resume following and hide the jump button.
        _autoScroll = true;
        if (ScrollDownButton is not null) ScrollDownButton.IsVisible = false;
    }

    /// <summary>
    /// Background model warm-up after connecting. Shows a "Loading model" status while
    /// Ollama pulls the weights into memory, then flips to Ready. Best-effort: never
    /// blocks the session or surfaces an error if it fails.
    /// </summary>
    private async Task WarmSelectedModelAsync()
    {
        var model = _selectedModel;
        if (_ollama is null || string.IsNullOrWhiteSpace(model))
        {
            SetStatus("Ready", AppDot.Idle);
            return;
        }

        SetStatus("Loading model (first run can take a minute)...", AppDot.Connecting);
        try
        {
            await _ollama.WarmAsync(model, (int)_assumedContextWindow);
        }
        catch { /* best-effort */ }

        // Only claim Ready if the session is still live and the agent did not start a
        // turn while we were warming (otherwise we'd stomp the "Agent working" status).
        if (_client is not null && !_agentRunning) SetStatus("Ready", AppDot.Idle);
    }

    /// <summary>
    /// Switch the live conversation to a new model by recreating it. The server's
    /// switch_llm endpoint is "first-write-wins" per usage_id and only swaps the
    /// agent (never the condenser), so on an existing conversation it silently keeps
    /// the original model. Tearing down and recreating with the new model baked into
    /// a fresh spec is the only reliable way to actually change models mid-session.
    /// </summary>
    private async Task RestartConversationWithModelAsync(string model)
    {
        SetBusy(true);
        try
        {
            await DisposeClientAsync();   // drop the stale conversation + its registry
            ResetChatView();              // the new conversation starts empty; clear the UI too

            var spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode, autoCompact: _settings.AutoCompact);
            ApplyModel(spec, model);
            if (_settings.AutoTune) await AutoTuneSpecAsync(spec);
            else await ApplyNativeToolCallingAsync(spec);   // keep tool calling correct

            await ConnectConversationAsync(spec);
            AddSystem($"Switched model to {model} - started a fresh conversation "
                      + "(history was reset because the model changed).");
        }
        catch (Exception ex)
        {
            SetStatus("Error", AppDot.Down);
            AddError($"Could not switch model: {ex.Message}");
            await DisposeClientAsync();
            SetSessionActive(false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Tear down the active conversation but leave the server running for reuse.</summary>
    private async Task StopSessionAsync()
    {
        SetBusy(true);
        StartButton.IsEnabled = false;
        SetStatus("Stopping...", AppDot.Connecting);
        try
        {
            if (_client is not null)
            {
                try { await _client.InterruptAsync(); } catch { }
            }
            await DisposeClientAsync();

            // Stop only halts the agent and ends the conversation. The model stays
            // resident (keep_alive=-1) so the next Start is instant; it is freed only
            // when the user switches models (the engine evicts it) or shuts the engine
            // down (the process is killed). This matches the desired behavior of a
            // pause button that does not pay the cold-load cost on every restart.
            AddSystem("Session stopped. Press Start to begin a new one.");
        }
        finally
        {
            SetSessionActive(false);
            SetStatus("Stopped", AppDot.Down);
            SetBusy(false);
        }
    }

    // Button click shims that forward to the awaitable send/steer implementations.
    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    private async void OnSteerClick(object sender, RoutedEventArgs e) => await SteerAsync();

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends, or queues the message when the agent is busy; Shift+Enter =
        // newline. To interject immediately mid-task, use the Steer button.
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    /// <summary>
    /// Stop the agent's in-flight task (pause) without ending the session. Also
    /// drops any queued messages, since the user is explicitly halting work.
    /// </summary>
    private async void OnInterruptClick(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        int dropped = _queue.Count;
        _queue.Clear();
        UpdateQueueIndicator();
        AddSystem(dropped > 0
            ? $"Stopping the agent's current task (cleared {dropped} queued message(s))..."
            : "Stopping the agent's current task...");
        await _client.InterruptAsync();
    }

    /// <summary>Manually trigger history summarization to reclaim context space.</summary>
    private async void OnCompactClick(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        try
        {
            AddSystem("Compacting conversation history...");
            SetCompactStatus("Compacting...", AppDot.Connecting);
            await _client.CondenseAsync();
        }
        catch (Exception ex)
        {
            AddError($"Compact failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remember the chosen model and refresh its capability strip. If a session is
    /// already live, recreate the conversation on the new model (the server cannot
    /// reliably swap a running conversation's model - see
    /// <see cref="RestartConversationWithModelAsync"/>).
    /// </summary>
    private async void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingModels) return;
        if (ModelCombo.SelectedItem is not ModelEntry entry || entry.IsEmbedding) return;
        var model = entry.Name;
        _selectedModel = model;

        // New model gets a fresh chance to prove it can drive the tools.
        _toolSchemaErrors = 0;
        _weakModelWarned = false;

        _ = UpdateCapabilitiesAsync(model);
        _ = ShowModelFitnessAdviceAsync(model);

        // Before Start the choice is just remembered; it is applied when the
        // conversation is created. Once connected, recreate the conversation with the
        // new model (the server cannot reliably swap a live conversation's model).
        if (_client is null) return;

        await RestartConversationWithModelAsync(model);
    }

    // ---- attachments --------------------------------------------------------

    private readonly List<Attachment> _attachments = new();

    /// <summary>Open a file picker and stage the chosen files as attachments.</summary>
    private async void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Attach files, images or video",
            AllowMultiple = true
        });
        if (files.Count > 0)
        {
            StagePaths(files.Select(f => f.Path.LocalPath));
        }
    }

    private void StagePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                if (!System.IO.File.Exists(path)) continue;  // ignore dropped folders
                _attachments.Add(AttachmentService.Stage(path, _workingDir));
            }
            catch (Exception ex)
            {
                AddError($"Could not attach {System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }
        RenderAttachChips();
    }

    private void OnInputDragEnter(object sender, DragEventArgs e)
    {
        e.DragEffects = AttachButton.IsEnabled && e.DataTransfer.TryGetFiles() is { Length: > 0 }
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        if (!AttachButton.IsEnabled) return;
        if (e.DataTransfer.TryGetFiles() is { } files)
        {
            var paths = files
                .OfType<Avalonia.Platform.Storage.IStorageFile>()
                .Select(f => f.Path.LocalPath)
                .ToArray();
            if (paths.Length > 0)
            {
                StagePaths(paths);
                e.Handled = true;
            }
        }
    }

    private void ClearAttachments()
    {
        _attachments.Clear();
        RenderAttachChips();
    }

    /// <summary>Rebuild the attachment chip tray from the current attachment list.</summary>
    private void RenderAttachChips()
    {
        AttachPanel.Children.Clear();
        if (_attachments.Count == 0)
        {
            AttachPanel.IsVisible = false;
            return;
        }
        AttachPanel.IsVisible = true;

        foreach (var att in _attachments.ToList())
        {
            var icon = att.Kind switch
            {
                AttachKind.Image => "\U0001F5BC",
                AttachKind.Video => "\U0001F3AC",
                _ => "\U0001F4CE"
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = icon,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = att.FileName,
                Foreground = (IBrush)this.FindResource("Text")!,
                FontSize = 12,
                Margin = new Thickness(5, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            var remove = new Button
            {
                Content = "✕",
                Foreground = (IBrush)this.FindResource("TextDim")!,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(remove, "Remove");
            
            var captured = att;
            remove.Click += (_, _) => { _attachments.Remove(captured); RenderAttachChips(); };
            row.Children.Add(remove);

            var chip = new Border
            {
                Background = (IBrush)this.FindResource("PanelAlt")!,
                BorderBrush = (IBrush)this.FindResource("Border")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row
            };
            ToolTip.SetTip(chip, att.DestRelPath);
            AttachPanel.Children.Add(chip);
        }
    }

    /// <summary>
    /// Turn the staged attachments into (1) a text note listing their paths so the
    /// agent can open them, and (2) image data URLs for vision models (including
    /// frames pulled from any videos). Clears the attachment tray.
    /// </summary>
    private async Task<(string note, List<string> images)> ConsumeAttachmentsAsync()
    {
        var note = new System.Text.StringBuilder();
        var images = new List<string>();
        bool vision = ModelHasVision;

        if (_attachments.Count > 0)
        {
            note.AppendLine();
            note.AppendLine("Attached files (in the working directory - open them with your tools):");
            foreach (var att in _attachments)
            {
                note.AppendLine($"- {att.DestRelPath}");

                if (att.Kind == AttachKind.Image && vision)
                {
                    try { images.Add(AttachmentService.ToDataUrl(att.DestAbsPath)); } catch { }
                }
                else if (att.Kind == AttachKind.Video)
                {
                    var probe = await AttachmentService.ProbeVideoAsync(att.DestAbsPath);
                    if (!string.IsNullOrWhiteSpace(probe))
                        note.AppendLine($"  (video info: {probe!.Replace("\n", "; ")})");

                    if (vision)
                    {
                        var frames = await AttachmentService.ExtractFramesAsync(att.DestAbsPath);
                        if (frames.Count > 0)
                        {
                            images.AddRange(frames);
                            note.AppendLine($"  ({frames.Count} frames sampled from this video are attached as images)");
                        }
                        else if (!AttachmentService.FfmpegAvailable)
                            note.AppendLine("  (install ffmpeg to let me see video frames)");
                    }
                }
            }

            if (images.Count == 0 && _attachments.Any(a => a.Kind != AttachKind.File) && !vision)
                note.AppendLine("(The current model has no vision capability, so images/video are "
                                + "placed on disk but cannot be viewed directly. Switch to a vision model to see them.)");
        }

        ClearAttachments();
        return (note.ToString(), images);
    }

    /// <summary>Detect the model's metadata and compute recommended settings.</summary>
    private async Task<(TuneResult rec, ModelInfo? info)> RecommendAsync(string model)
    {
        ModelInfo? info = _ollama is null ? null : await _ollama.GetModelInfoAsync(model);

        // VRAM-aware auto-fit: only when the user has NOT pinned an explicit context
        // cap in Settings (their value always wins). We size the window so the model
        // weights + KV cache stay resident in VRAM (GPU) or system RAM (CPU), which
        // is what stops big models from saturating a too-large window and stalling.
        int autoFit = 0;
        if (_settings.ContextLength <= 0 && _ollama is not null)
        {
            long weightBytes = await _ollama.GetModelDiskSizeAsync(model);
            // FitContextToMemory probes VRAM/RAM, which on first call spawns
            // nvidia-smi / PowerShell and blocks for up to a few seconds. Run it off
            // the UI thread so the start sequence never freezes the window. Results
            // are cached for the app lifetime, so later calls are effectively free.
            var device = _settings.ComputeDevice;
            var kv = _settings.KvCacheType;
            autoFit = await Task.Run(() => ModelTuning.FitContextToMemory(info, weightBytes, device, kv));
        }

        // ContextLength is the user-set ceiling; AutoTune fits each model under it.
        return (ModelTuning.Recommend(model, info, _settings.ContextLength, autoFit), info);
    }

    /// <summary>
    /// Apply newbie-friendly auto settings (temperature, reasoning, context) to the
    /// spec before a session starts, and adjust the app's context length to match.
    /// </summary>
    private async Task AutoTuneSpecAsync(JsonNode spec)
    {
        var model = _selectedModel ?? StripProvider(AgentSpecProvider.DescribeModel(spec));
        if (string.IsNullOrWhiteSpace(model)) return;

        var (rec, info) = await RecommendAsync(model);
        ApplyTuningToLlm(spec["llm"], rec);
        ApplyTuningToLlm(spec["condenser"]?["llm"], rec);

        // Resize the engine to the tuned window and apply the matching token budget
        // (input cap + condenser trigger) so the agent compacts before the engine has
        // to truncate, and so the meter tracks the real window.
        await ApplyRuntimeContextAsync(spec, rec.ContextLength);

        var kind = ModelTuning.IsThinkingModel(model, info) ? "thinking"
            : model.Contains("coder", StringComparison.OrdinalIgnoreCase) ? "coder"
            : "chat";

        // When auto-fitting (no explicit Settings cap), note what memory the context
        // window was sized against so the user understands why it landed where it did.
        var fitNote = "";
        if (_settings.ContextLength <= 0)
        {
            var budget = HardwareInfo.DescribeBudget(_settings.ComputeDevice);
            if (!string.IsNullOrEmpty(budget)) fitNote = $", fit to {budget}";
        }
        AddSystem($"Auto-tuned {model} ({kind}): temperature {rec.Temperature:0.0}, "
                  + $"context {rec.ContextLength:N0} tokens{fitNote}, reasoning {rec.ReasoningEffort}.");
    }

    /// <summary>
    /// Resolve native tool calling for the spec independently of AutoTune. The saved
    /// agent_settings.json can persist native_tool_calling=false (the prompt-text
    /// fallback), which makes tool-capable local models leak '&lt;function=...&gt;'
    /// markup, loop, and stall doing nothing. Tool-calling mode is a correctness
    /// setting, not a tuning preference, so we always set it from the model's real
    /// capabilities - including when AutoTune is off, which otherwise skips
    /// <see cref="AutoTuneSpecAsync"/> entirely and would leave the broken false.
    /// </summary>
    private async Task ApplyNativeToolCallingAsync(JsonNode spec)
    {
        var model = _selectedModel ?? StripProvider(AgentSpecProvider.DescribeModel(spec));
        if (string.IsNullOrWhiteSpace(model)) return;

        ModelInfo? info = _ollama is null ? null : await _ollama.GetModelInfoAsync(model);
        bool native = ModelTuning.SupportsTools(info);
        SetNativeToolCalling(spec["llm"], native);
        SetNativeToolCalling(spec["condenser"]?["llm"], native);
    }

    private static void SetNativeToolCalling(JsonNode? llm, bool native)
    {
        if (llm is JsonObject o) o["native_tool_calling"] = native;
    }

    /// <summary>
    /// Make the whole stack agree on one runtime context window: restart the engine
    /// to <paramref name="window"/> if needed, point the meter at it, and apply the
    /// matching token budget to the spec. Used by both the AutoTune and AutoTune-off
    /// start paths so a manual "Max context" entry (or the auto-fit) always reaches
    /// the engine and the agent.
    /// </summary>
    private async Task ApplyRuntimeContextAsync(JsonNode spec, int window)
    {
        if (window < ModelTuning.ContextFloor) window = ModelTuning.ContextFloor;
        await EnsureEngineContextAsync(window);
        // The runtime window is what the meter should track. We do NOT overwrite
        // _settings.ContextLength here - that is the user's ceiling, not the result.
        _assumedContextWindow = window;
        ApplyContextBudget(spec, window);
    }

    /// <summary>
    /// Relaunch the bundled engine so its fixed context window (OLLAMA_CONTEXT_LENGTH,
    /// applied at process launch) matches <paramref name="window"/>. Without this a
    /// reused engine keeps its previous size: the agent's requests then run at the
    /// wrong window, the model reloads, and the meter "snaps back" to the launch
    /// default. Guarded by _client is null so a mid-session model switch never kills a
    /// live engine, and only acts on the bundled engine URL. Cheap at start time: no
    /// model is loaded until the first inference, and health is re-awaited before the
    /// session connects so the backend never talks to a dead port.
    /// </summary>
    private async Task EnsureEngineContextAsync(int window)
    {
        if (_client is null
            && StayVibinEngineManager.IsDefaultEngineUrl(_settings.OllamaUrl)
            && _engineContext != window)
        {
            SetStatus("Resizing engine context...", AppDot.Connecting);
            RebuildEngineManager(window);
            await _engine!.StartAsync(TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Size the agent's token budget to the fixed engine window so the engine never
    /// has to truncate the prompt (the "truncating input prompt" / "context buffer
    /// filled up" symptom). Two levers:
    ///  - max_input_tokens = window minus an output reserve, so the input can never
    ///    grow to fill the whole window and leave no room for the model's reply.
    ///  - condenser max_tokens (token-aware auto-compaction) below the input budget,
    ///    with headroom for one large tool observation, so history is summarized
    ///    BEFORE the next prompt would exceed the budget. Without this the condenser
    ///    only fires on event COUNT (max_size), which a few big tool outputs skip.
    /// When auto-compaction is off we leave max_tokens null so nothing triggers
    /// automatically (the user compacts manually via the context ring).
    /// </summary>
    private void ApplyContextBudget(JsonNode spec, int window)
    {
        // Reserve ~12% of the window (clamped 1-8k) for the model's reply.
        int outputReserve = Math.Clamp(window / 8, 1024, 8192);
        int inputBudget = Math.Max(window - outputReserve, ModelTuning.ContextFloor);

        SetLlmInputBudget(spec["llm"], inputBudget);
        SetLlmInputBudget(spec["condenser"]?["llm"], inputBudget);

        if (spec["condenser"] is JsonObject cond)
        {
            if (_settings.AutoCompact)
            {
                // Compact at 80% of the input budget: enough headroom that a single
                // large tool observation cannot push the next prompt past the budget
                // before the condenser runs on the following step.
                int trigger = (int)(inputBudget * 0.80);
                if (trigger < ModelTuning.ContextFloor) trigger = ModelTuning.ContextFloor;
                cond["max_tokens"] = trigger;
            }
            else
            {
                cond["max_tokens"] = null;
            }
        }
    }

    private static void SetLlmInputBudget(JsonNode? llm, int inputBudget)
    {
        if (llm is JsonObject o) o["max_input_tokens"] = inputBudget;
    }

    private static void ApplyTuningToLlm(JsonNode? llm, TuneResult rec)
    {
        if (llm is not JsonObject o) return;
        o["temperature"] = rec.Temperature;
        o["reasoning_effort"] = rec.ReasoningEffort;
        // NOTE: max_input_tokens is intentionally NOT set here. The runtime input
        // budget (window minus an output reserve) is applied centrally in
        // ApplyContextBudget so the engine always keeps room for the reply.

        // Use Ollama's real tool API when the model supports it. The prompt-text
        // fallback (native off) is what makes capable models leak '<function=...>'
        // markup, loop, and tell the user to run commands instead of acting.
        o["native_tool_calling"] = rec.NativeToolCalling;

        // Stream agent replies token-by-token; keep condenser non-streaming.
        if (o["usage_id"]?.GetValue<string>() == "agent")
            o["stream"] = true;

        // Local Ollama does not use encrypted-reasoning channels; turning this off
        // avoids odd behavior on DeepSeek-R1 and similar models.
        var baseUrl = o["base_url"]?.GetValue<string>() ?? "";
        if (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            o["enable_encrypted_reasoning"] = false;

        // Best-effort hint so native-ollama paths size their context correctly.
        if (o["litellm_extra_body"] is not JsonObject extra)
        {
            extra = new JsonObject();
            o["litellm_extra_body"] = extra;
        }
        extra["num_ctx"] = rec.ContextLength;
    }

    /// <summary>Prefix user messages with editor context (Cursor-style focus hint).</summary>
    private string PrefixEditorContext(string text)
    {
        if (_editorPath is null || !File.Exists(_editorPath)) return text;
        try
        {
            var rel = Path.GetRelativePath(_workingDir, _editorPath);
            if (rel.StartsWith("..")) rel = _editorPath;
            rel = rel.Replace('\\', '/');
            var line = CodeEditor.TextArea.Caret.Line;
            return $"[Context: user has {rel} open in the editor (line {line})]\n\n{text}";
        }
        catch
        {
            return text;
        }
    }


    // ---- agent stream handling ---------------------------------------------

    /// <summary>Build the bubble text shown to the user (message plus attachment list).</summary>
    private string ComposeShown(string text, bool hasAttachments)
        => hasAttachments
            ? (text.Length > 0 ? text + "\n" : "")
              + string.Join("\n", _attachments.Select(a => $"[{a.Kind}] {a.FileName}"))
            : text;

    /// <summary>
    /// Send the typed message. If the agent is already working, the message is
    /// queued and sent automatically when the current task finishes (use Steer to
    /// interject immediately instead).
    /// </summary>
    private async Task SendAsync()
    {
        var text = InputBox.Text?.Trim() ?? "";
        bool hasAttachments = _attachments.Count > 0;
        if ((text.Length == 0 && !hasAttachments) || _client is null || _busy) return;

        // The conversation now has real content, so it must not be auto-discarded.
        _activeConvHadInput = true;

        // A typed message is the operator's response to a pending plan (an approval
        // word or a change request), so dismiss the approval bar - the agent reads
        // the message and acts on it.
        if (_planAwaitingApproval)
        {
            _planAwaitingApproval = false;
            SetPlanApprovalVisible(false);
        }

        var shown = ComposeShown(text, hasAttachments);
        InputBox.Clear();

        // Capture attachments now (staging clears the tray), so each queued message
        // keeps its own files/images.
        var (note, images) = await ConsumeAttachmentsAsync();
        var payload = text + note;

        if (_agentRunning)
        {
            var queuedBubble = AddItem(ChatRole.User, "You (queued)", shown);
            _queue.Enqueue(new QueuedMessage(queuedBubble, payload, images));
            UpdateQueueIndicator();
            return;
        }

        AddUser(shown);
        _streamingItem = null;
        ResetTurnTracking();
        SetRunning(true);

        try
        {
            await _client.SendUserMessageAsync(PrefixEditorContext(payload), images);
        }
        catch (Exception ex)
        {
            AddError($"Failed to send: {ex.Message}");
            SetRunning(false);
            await FlushQueueAsync();
        }
    }

    /// <summary>
    /// Inject a message while the agent is still working. The running loop picks
    /// it up on its next step, so you can nudge it without stopping it. Unlike a
    /// normal send, a steer skips the queue and goes through right away.
    /// </summary>
    private async Task SteerAsync()
    {
        var text = InputBox.Text?.Trim() ?? "";
        bool hasAttachments = _attachments.Count > 0;
        if ((text.Length == 0 && !hasAttachments) || _client is null) return;

        _activeConvHadInput = true;   // real content; do not auto-discard this chat
        var shown = ComposeShown(text, hasAttachments);
        InputBox.Clear();
        AddItem(ChatRole.User, "You (steer)", shown);
        try
        {
            var (note, images) = await ConsumeAttachmentsAsync();
            await _client.SendUserMessageAsync(PrefixEditorContext(text + note), images);
        }
        catch (Exception ex)
        {
            AddError($"Failed to steer: {ex.Message}");
        }
    }

    /// <summary>
    /// Send the next queued message, if any, now that the agent is idle. Called on
    /// the running-&gt;idle transition so queued messages fire one at a time.
    /// </summary>
    private async Task FlushQueueAsync()
    {
        if (_client is null || _agentRunning || _queue.Count == 0) return;

        await _flushLock.WaitAsync();
        try
        {
            if (_client is null || _agentRunning || _queue.Count == 0) return;

            // Peek first so a failed send does not drop the message from the queue.
            var msg = _queue.Peek();
            _streamingItem = null;
            ResetTurnTracking();
            SetRunning(true);
            try
            {
                await _client.SendUserMessageAsync(PrefixEditorContext(msg.Text), msg.Images);
                _queue.Dequeue();
                msg.Bubble.Header = "You";
                UpdateQueueIndicator();
            }
            catch (Exception ex)
            {
                AddError($"Failed to send queued message: {ex.Message}");
                msg.Bubble.Header = "You (queued)";
                SetRunning(false);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>Show or hide the "N queued" indicator next to the input buttons.</summary>
    private void UpdateQueueIndicator()
    {
    }

    /// <summary>
    /// Check just the freshly appended tail of the stream for the start of any marker
    /// we must hide from the live bubble: a leaked tool-call tag (&lt;function=,
    /// &lt;parameter=) or inlined reasoning (&lt;think&gt;, or a harmony control token
    /// "&lt;|"). The overlap (longest marker is "&lt;parameter=" at 11 chars) ensures a
    /// marker split across two deltas is still detected.
    /// </summary>
    private bool TailHasHiddenMarker(int newChars)
    {
        const int overlap = 12;
        int len = _streamRaw.Length;
        int from = Math.Max(0, len - newChars - overlap);
        var tail = _streamRaw.ToString(from, len - from);
        return tail.IndexOf("<function=", StringComparison.OrdinalIgnoreCase) >= 0
            || tail.IndexOf("<parameter=", StringComparison.OrdinalIgnoreCase) >= 0
            || tail.IndexOf("<think>", StringComparison.OrdinalIgnoreCase) >= 0
            || tail.IndexOf("<|", StringComparison.Ordinal) >= 0;
    }

    /// <summary>Start a fresh accounting window for the next agent turn.</summary>
    private void ResetTurnTracking()
    {
        _turnUsedTools = false;
        _turnAutoNudged = false;
        _lastAssistantText = "";
        _lastAgentItem = null;
    }

    private void OnAgentUpdate(AgentUpdate u)
        => Dispatcher.UIThread.Post(() => ApplyUpdate(u));

    private void ApplyUpdate(AgentUpdate u)
    {
        if (u.Role == ChatRole.Agent && u.IsDelta)
        {
            if (_streamingItem is null)
            {
                _streamingItem = AddItem(ChatRole.Agent, u.Header, "");
                _lastAgentItem = _streamingItem;
                _streamRaw.Clear();
                _streamStripMode = false;
                _streamEnvelopeMode = false;
            }
            _streamRaw.Append(u.Text);

            // A model sometimes wraps its whole reply in a JSON envelope, e.g.
            // {"message":"...","summary":"..."}. As soon as we recognize that shape,
            // render only the extracted message so the braces/keys never flash. The
            // final MessageEvent later replaces this with the authoritative answer.
            if (!_streamStripMode)
            {
                var env = ChatText.StreamingEnvelopeProse(_streamRaw.ToString());
                if (env is not null)
                {
                    _streamEnvelopeMode = true;
                    _streamingItem.Text = env;
                    ScrollToBottom();
                    return;
                }
                if (_streamEnvelopeMode)
                {
                    // Was an envelope but the latest tail no longer matches; keep the
                    // last good text rather than reverting to raw braces.
                    ScrollToBottom();
                    return;
                }
            }

            // Fast path: until hidden markup actually appears, append incrementally
            // (cheap). A misbehaving model leaks <function=.../<parameter=..., and a
            // reasoning model inlines its chain-of-thought (<think>...</think> or the
            // gpt-oss harmony "analysis" channel). Once any of those appears we switch
            // to re-cleaning the whole stream each delta so neither tool plumbing nor
            // raw reasoning syntax ever flashes in the live bubble - the reasoning is
            // surfaced separately as a clean "Thinking" block by the final message. We
            // scan only the tail (new chunk plus a small overlap) so detection stays
            // O(1) per delta and still catches a marker split across two deltas.
            if (!_streamStripMode && TailHasHiddenMarker(u.Text.Length))
                _streamStripMode = true;

            if (_streamStripMode)
                // SplitReasoning's prose also strips tool markup, so this is a superset
                // of Strip: it cleans tool syntax AND drops inlined reasoning.
                _streamingItem.Text = ChatText.SplitReasoning(_streamRaw.ToString()).prose;
            else
                _streamingItem.Append(u.Text);

            ScrollToBottom();
            return;
        }

        if (u.Role == ChatRole.Agent && !u.IsDelta)
        {
            // Final assistant message. Replace any streamed draft with the
            // authoritative text, otherwise add a fresh bubble.
            if (_streamingItem is not null)
            {
                _streamingItem.Text = u.Text;
                _lastAgentItem = _streamingItem;
                _streamingItem = null;
            }
            else
            {
                _lastAgentItem = AddItem(ChatRole.Agent, u.Header, u.Text);
            }
            _lastAssistantText = u.Text;
            ScrollToBottom();
            return;
        }

        // Thought / Tool / Observation / Error all break the current stream.
        _streamingItem = null;
        if (u.Role is ChatRole.Tool or ChatRole.Observation)
            _turnUsedTools = true;
        // A successful observation means the model used a tool correctly, so reset
        // the bad-tool-call streak.
        if (u.Role is ChatRole.Observation)
            _toolSchemaErrors = 0;

        AddItem(u.Role, u.Header, u.Text);

        if (u.Role is ChatRole.Error)
            NoteToolSchemaError(u.Text);

        ScrollToBottom();
    }

    /// <summary>
    /// Detect the "model put extra fields in a tool call" failure (pydantic rejects
    /// it with extra_forbidden / validation errors) and, after it repeats, explain
    /// the real cause: the model is attaching fields the tool does not accept - most
    /// often its own reasoning (gpt-oss emits an "analysis" channel) or invented
    /// params like "reset". This is a formatting mismatch between the model's
    /// tool-call output and the Ollama/runtime bridge, NOT a model-size problem
    /// (big models hit it too). We suggest concrete, model-appropriate fixes.
    /// </summary>
    private void NoteToolSchemaError(string errorText)
    {
        if (!LooksLikeToolSchemaError(errorText))
            return;

        _toolSchemaErrors++;
        if (_weakModelWarned || _toolSchemaErrors < 2)
            return;

        _weakModelWarned = true;
        var model = _selectedModel ?? "This model";
        bool wrongToolName = LooksLikeUnknownTool(errorText);
        bool leaksReasoning = errorText.Contains("analysis", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase);

        string msg;
        if (wrongToolName)
        {
            // The model is calling a tool name that does not exist (e.g.
            // 'execute_powershell'). The shell IS available - as the tool named
            // 'terminal' - so this is the model mis-naming the tool, not a missing
            // capability.
            msg = $"Heads up: {model} keeps calling tools that do not exist (for example "
                + "a separate \"powershell\" or \"bash\" tool). StayVibin's agent already "
                + "has full PowerShell/CLI access through its built-in 'terminal' tool - "
                + "the shell is not missing. The model is just naming the tool wrong, so "
                + "its commands fail. This is a tool-calling reliability problem with the "
                + "model, not your setup.";
        }
        else
        {
            msg = $"Heads up: {model} keeps adding fields to its tool calls that the tool "
                + "does not accept (a schema validation error). This is a formatting "
                + "mismatch between the model's tool-call output and the local runtime - "
                + "not a model-size issue, and not a bug in your setup.";
            if (leaksReasoning)
                msg += " In this case the model is putting its internal reasoning (the "
                    + "\"analysis\" channel) inside the tool call instead of in a separate "
                    + "field. gpt-oss is known to do this through Ollama.";
        }

        msg += " The reliable fix is a model with clean native tool-calling - "
            + SuggestCleanToolModels() + " behave well here. You can keep working; "
            + "StayVibin retries automatically, but the run will be unreliable until the "
            + "tool calls come through clean.";

        AddSystem(msg);
    }

    /// <summary>True when the error is "the model called a tool that does not exist".</summary>
    private static bool LooksLikeUnknownTool(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var s = text.ToLowerInvariant();
        return s.Contains("not found") && (s.Contains("available:") || s.Contains("tool '"));
    }

    /// <summary>
    /// A short list of models that drive the tools cleanly, excluding whatever model
    /// is currently selected (so we never recommend the one that is failing).
    /// </summary>
    private string SuggestCleanToolModels()
    {
        string[] clean = ["qwen2.5-coder:14b", "qwen2.5-coder:32b", "qwen3:14b", "llama3.1:8b"];
        var current = _selectedModel ?? "";
        var picks = clean
            .Where(m => !m.Equals(current, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return picks.Length == 2 ? $"{picks[0]} or {picks[1]}" : string.Join(", ", picks);
    }

    /// <summary>True for tool-call schema validation errors (wrong/extra parameters).</summary>
    private static bool LooksLikeToolSchemaError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var s = text.ToLowerInvariant();
        return s.Contains("extra_forbidden")
            || s.Contains("extra inputs are not permitted")
            || s.Contains("validation error")
            || s.Contains("error validating tool")
            || (s.Contains("not found") && s.Contains("available:"));   // unknown tool name
    }

    private void OnServerStatus(string status)
        => Dispatcher.UIThread.Post(() =>
        {
            var s = status.ToLowerInvariant();
            bool running = s.Contains("running");
            bool waitingForPermission = s.Contains("waiting_for_confirmation");
            bool falling = _agentRunning && !running;   // a turn just finished
            bool rising = !_agentRunning && running;    // a new turn just started
            SetRunning(running);
            if (rising) _stuckNotifiedThisTurn = false; // arm stuck detection for this turn
            if (waitingForPermission)
            {
                SetStatus("Waiting for permission", AppDot.Connecting);
                EnterPermissionApproval();
            }
            else if (!running)
            {
                SetStatus(Capitalize(status), AppDot.Idle);
                if (falling && !_permissionAwaitingApproval)
                {
                    _ = HandleTurnFinishedAsync();
                }
            }
            else
                SetStatus("Agent working...", AppDot.Working);
        });

    /// <summary>
    /// Post-turn maintenance plus a guard for a common weak local-model failure:
    /// the assistant says "I'll inspect..." then finishes without invoking a tool.
    /// In that case, send one internal nudge so the app keeps working instead of
    /// leaving the user with a stopped "I'll do it" promise.
    /// </summary>
    private async Task HandleTurnFinishedAsync()
    {
        _ = UpdateRepoBadgeAsync();   // the agent may have committed/branched
        _ = RefreshTreeAsync();       // surface any files the agent changed

        // A freshly created conversation gets its title auto-generated from the first
        // user message; refresh the sidebar once so it stops showing "New chat".
        if (_pendingTitleConvId is not null && _pendingTitleConvId == _activeConversationId)
        {
            _pendingTitleConvId = null;
            _ = RefreshConversationListAsync();
        }

        // A plan awaiting approval takes priority: show the Approve bar and stop
        // here (do not auto-continue or flush the queue while we wait on the user).
        if (_permissionAwaitingApproval || TryEnterPlanApproval())
            return;

        if (ShouldAutoContinueWork())
        {
            await AutoContinueWorkAsync();
            return;
        }

        await FlushQueueAsync();      // send the next queued message, if any
    }

    /// <summary>
    /// If Plan Mode is active and the agent ended its turn with the PLAN_READY
    /// marker, strip the marker from the bubble, reveal the Approve/Request-changes
    /// bar, and report that we are now waiting on the operator.
    /// </summary>
    private bool TryEnterPlanApproval()
    {
        if (_settings.PlanMode == PlanMode.Off) return false;

        var item = _lastAgentItem;
        var text = item?.Text ?? _lastAssistantText;
        if (string.IsNullOrEmpty(text)
            || !text.Contains(AgentSpecProvider.PlanReadyMarker, StringComparison.Ordinal))
            return false;

        if (item is not null)
            item.Text = StripPlanMarker(item.Text);

        _planAwaitingApproval = true;
        SetPlanApprovalVisible(true);
        return true;
    }

    private static string StripPlanMarker(string text)
        => text.Replace(AgentSpecProvider.PlanReadyMarker, "", StringComparison.Ordinal).TrimEnd();

    private void SetPlanApprovalVisible(bool visible)
        => PlanApprovalBar.IsVisible = visible;

    private static AgentPermissionPolicy ToAgentPermissionPolicy(PermissionMode mode)
        => mode == PermissionMode.AllowAll
            ? AgentPermissionPolicy.AllowAll
            : AgentPermissionPolicy.Ask;

    private void EnterPermissionApproval()
    {
        if (_settings.PermissionMode == PermissionMode.AllowAll) return;
        _permissionAwaitingApproval = true;
        PermissionText.Text =
            "The agent wants to perform an action StayVibin marked as risky or unknown. "
            + "Review the last tool/action above, then allow it once, deny it, or switch to Allow all.";
        PermissionApprovalBar.IsVisible = true;
    }

    private void ClearPermissionApproval()
    {
        _permissionAwaitingApproval = false;
        PermissionApprovalBar.IsVisible = false;
    }

    private async void OnPermissionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPermissionEvent) return;

        _settings.PermissionMode = PermissionModeCombo.SelectedIndex == 1
            ? PermissionMode.AllowAll
            : PermissionMode.Ask;
        _settings.Save();

        if (_client is not null)
        {
            try
            {
                await _client.SetConfirmationPolicyAsync(ToAgentPermissionPolicy(_settings.PermissionMode));
                AddSystem(_settings.PermissionMode == PermissionMode.AllowAll
                    ? "Permission mode set to Allow all. The agent can proceed without approval prompts for this session."
                    : "Permission mode set to Ask. Risky or unknown actions will pause for approval.");
            }
            catch (Exception ex)
            {
                AddError($"Could not update permission mode: {ex.Message}");
            }
        }
    }

    private async void OnPermissionAllow(object sender, RoutedEventArgs e)
    {
        if (!_permissionAwaitingApproval || _client is null) return;

        ClearPermissionApproval();
        SetRunning(true);
        try
        {
            await _client.RespondToConfirmationAsync(true);
        }
        catch (Exception ex)
        {
            AddError($"Could not allow action: {ex.Message}");
            SetRunning(false);
        }
    }

    private async void OnPermissionDeny(object sender, RoutedEventArgs e)
    {
        if (!_permissionAwaitingApproval || _client is null) return;

        ClearPermissionApproval();
        SetRunning(true);
        try
        {
            await _client.RespondToConfirmationAsync(false, "Operator denied this action.");
        }
        catch (Exception ex)
        {
            AddError($"Could not deny action: {ex.Message}");
            SetRunning(false);
        }
    }

    private async void OnPermissionAllowAll(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;

        _settings.PermissionMode = PermissionMode.AllowAll;
        _settings.Save();
        _suppressPermissionEvent = true;
        PermissionModeCombo.SelectedIndex = 1;
        _suppressPermissionEvent = false;

        try
        {
            await _client.SetConfirmationPolicyAsync(AgentPermissionPolicy.AllowAll);
            if (_permissionAwaitingApproval)
            {
                ClearPermissionApproval();
                SetRunning(true);
                await _client.RespondToConfirmationAsync(true);
            }
            AddSystem("Permission mode set to Allow all. The agent can proceed without approval prompts.");
        }
        catch (Exception ex)
        {
            AddError($"Could not allow all actions: {ex.Message}");
            SetRunning(false);
        }
    }

    /// <summary>Operator changed the Plan Mode selector: persist it and note when it applies.</summary>
    private void OnPlanModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlanEvent) return;

        _settings.PlanMode = (PlanMode)PlanModeCombo.SelectedIndex;
        _settings.Save();

        // Turning planning off clears any pending approval prompt.
        if (_settings.PlanMode == PlanMode.Off && _planAwaitingApproval)
        {
            _planAwaitingApproval = false;
            SetPlanApprovalVisible(false);
        }

        // The mode is baked into the agent's system prompt at conversation creation,
        // so a live session keeps its current behavior until the next fresh start.
        if (_client is not null)
            AddSystem($"Plan Mode set to {_settings.PlanMode}. It takes effect the next "
                      + "time a conversation starts (press Start, or switch model).");
    }

    /// <summary>Approve the pending plan: tell the agent to carry it out now.</summary>
    private async void OnPlanApprove(object sender, RoutedEventArgs e)
    {
        if (!_planAwaitingApproval || _client is null) return;

        _planAwaitingApproval = false;
        SetPlanApprovalVisible(false);
        AddItem(ChatRole.User, "You", "Approved - proceed with the plan.");
        SetRunning(true);
        try
        {
            await _client.SendUserMessageAsync(
                "Approved. Carry out the plan now and make the changes. Do not stop to "
                + "ask again unless you hit something genuinely risky or destructive.");
        }
        catch (Exception ex)
        {
            AddError($"Failed to approve plan: {ex.Message}");
            SetRunning(false);
        }
    }

    /// <summary>Reject the pending plan and let the operator type what to change.</summary>
    private void OnPlanDeny(object sender, RoutedEventArgs e)
    {
        if (!_planAwaitingApproval) return;

        _planAwaitingApproval = false;
        SetPlanApprovalVisible(false);
        AddSystem("Plan declined. Tell the agent what to change, then press Send.");
        InputBox.Focus();
    }

    private bool ShouldAutoContinueWork()
    {
        if (_client is null || _queue.Count > 0 || _turnUsedTools || _turnAutoNudged
            || _planAwaitingApproval)
            return false;

        var text = (_lastAssistantText.Length > 0
            ? _lastAssistantText
            : _streamingItem?.Text ?? "").Trim();
        if (text.Length == 0) return false;

        return LooksLikeStoppedPromise(text) || LooksLikeUnevidencedCodeAnswer(text);
    }

    /// <summary>
    /// Heuristic for "future-tense work promise" responses. We keep it narrow so
    /// normal answers do not get continued, and only use it when no tools ran.
    /// </summary>
    private static bool LooksLikeStoppedPromise(string text)
    {
        var s = text.ToLowerInvariant();
        string[] promises =
        [
            // "I'll / I will <act>"
            "i'll start", "i will start", "i'll begin", "i will begin",
            "i'll examine", "i will examine", "i'll review", "i will review",
            "i'll look", "i will look", "i'll check", "i will check",
            "i'll inspect", "i will inspect", "i'll explore", "i will explore",
            "i'll investigate", "i will investigate", "i'll analyze", "i will analyze",
            "i'll go ahead", "i will go ahead",
            // "I'm/I am going to <act>"
            "i'm going to", "i am going to", "i'm gonna",
            // "Let me <act>" - the common mistral/llama phrasing
            "let me start", "let me begin", "let me examine", "let me review",
            "let me look", "let me check", "let me inspect", "let me explore",
            "let me investigate", "let me analyze", "let me open", "let me read",
            "let me run", "let me take a look", "let me go ahead", "let me dig",
            // "Let's <act>" / sequencing
            "let's start", "let's begin", "let's take a look",
            "next, i'll", "next i will", "then i'll", "then i will",
            "first, i'll", "first i'll", "first, i will", "to start, i",
            // common stop-and-defer tails
            "if i find anything", "i'll provide feedback", "i will provide feedback",
            "let me know if there's anything specific", "let me know if there is anything specific"
        ];
        return promises.Any(p => s.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Heuristic for a factual/code answer that sounds confident but does not mention
    /// any repository evidence. This catches the common weak-model failure mode where
    /// the assistant answers from priors after too little reading. We only use it
    /// when no tools ran this turn, so a real evidence-based answer is never retried.
    /// </summary>
    private static bool LooksLikeUnevidencedCodeAnswer(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        var lower = s.ToLowerInvariant();

        bool mentionsEvidence =
            lower.Contains(".cs") || lower.Contains(".cpp") || lower.Contains(".h")
            || lower.Contains(".xaml") || lower.Contains(".py") || lower.Contains("src\\")
            || lower.Contains("services\\") || lower.Contains("models\\")
            || lower.Contains("lines ") || lower.Contains("line ")
            || lower.Contains("i searched") || lower.Contains("i read")
            || lower.Contains("based on") || lower.Contains("in `") || lower.Contains("path:");

        bool makesCodeClaims =
            lower.Contains("issue") || lower.Contains("bug") || lower.Contains("uses ")
            || lower.Contains("netcode") || lower.Contains("function")
            || lower.Contains("method") || lower.Contains("class")
            || lower.Contains("cvar") || lower.Contains("spawn")
            || lower.Contains("answer") || lower.Contains("there isn't")
            || lower.Contains("there is no") || lower.Contains("the problem");

        return makesCodeClaims && !mentionsEvidence;
    }

    private async Task AutoContinueWorkAsync()
    {
        if (_client is null) return;

        _turnAutoNudged = true;
        var text = (_lastAssistantText.Length > 0
            ? _lastAssistantText
            : _streamingItem?.Text ?? "").Trim();
        bool needsEvidence = LooksLikeUnevidencedCodeAnswer(text);
        AddSystem(needsEvidence
            ? "The assistant answered without showing enough repository evidence, so StayVibin is asking it to verify the answer with tools."
            : "The assistant stopped after saying it would work, so StayVibin is continuing the turn automatically.");
        SetRunning(true);
        try
        {
            var nudge = needsEvidence
                ? "Verify your answer with tools before replying. Search and read the repository now, "
                    + "then answer with concrete findings grounded in real files and paths. Do not rely "
                    + "on prior knowledge or general advice."
                : "Continue now and actually perform the work with your tools. Do not describe a plan "
                    + "or ask me what to focus on. Start by inspecting the repository, running commands, "
                    + "and reporting concrete findings from real files.";
            await _client.SendUserMessageAsync(nudge);
        }
        catch (Exception ex)
        {
            AddError($"Failed to continue automatically: {ex.Message}");
            SetRunning(false);
            await FlushQueueAsync();
        }
    }

    private void OnDisconnected(string reason)
        => Dispatcher.UIThread.Post(() =>
        {
            AddSystem($"Disconnected: {reason}");
            SetRunning(false);
            SetStatus("Disconnected", AppDot.Down);
            _ = HandleDisconnectAsync();
        });

    /// <summary>WebSocket dropped: tear down the dead client so the UI matches reality.</summary>
    private async Task HandleDisconnectAsync()
    {
        await DisposeClientAsync();
        SetSessionActive(false);
    }

    // Best-guess runtime context window for the meter until the server reports real
    // stats. Set from the user's ceiling in the constructor, then per-model on tune.
    private long _assumedContextWindow = ModelTuning.FallbackContextLength;
    private UsageStats? _lastStats;

    private void OnStats(UsageStats s)
        => Dispatcher.UIThread.Post(() =>
        {
            _lastStats = s;
            RefreshStatsDisplay();
        });

    /// <summary>
    /// Recompute the meter's fallback window from the selected model and current
    /// Settings cap. Called after Settings saves so the UI updates immediately,
    /// without waiting for a new stats event or app restart.
    /// </summary>
    private async Task RefreshAssumedContextWindowAsync()
    {
        var model = (ModelCombo.SelectedItem as ModelEntry)?.Name ?? _selectedModel;
        if (_settings.AutoTune && !string.IsNullOrWhiteSpace(model))
        {
            // AutoTune resolves auto/cap against the model's native window.
            var (rec, _) = await RecommendAsync(model);
            _assumedContextWindow = rec.ContextLength;
        }
        else
        {
            // AutoTune off: explicit cap if set, otherwise the 32k auto default.
            _assumedContextWindow = _settings.BackendContextLength;
        }
    }

    /// <summary>
    /// Paint token/context stats. The meter denominator always reflects the
    /// configured runtime window (_assumedContextWindow), not the server's last
    /// stats snapshot (which can lag after a Settings change).
    /// </summary>
    private void RefreshStatsDisplay()
    {
        var stats = _lastStats;
        var window = _assumedContextWindow;
        // Prefer the live per-turn count from the engine (updates token-by-token);
        // fall back to the websocket snapshot when no turn is active.
        var used = _liveTurnTokens ?? stats?.PerTurnTokens ?? 0;
        var pct = window > 0 ? Math.Clamp(100.0 * used / window, 0, 100) : 0;

        // Drive the Cursor-style ring: sweep 0..360 degrees with usage and tint it
        // amber/red as it nears the limit. The numeric readout sits beside it.
        ContextArc.SweepAngle = pct * 3.6;
        ContextArc.Stroke = pct >= 90 ? (IBrush)this.FindResource("Err")!
            : pct >= 75 ? (IBrush)this.FindResource("Warn")!
            : (IBrush)this.FindResource("Accent")!;
        ContextText.Text = $"{Human(used)} / {Human(window)} ({pct:0}%)";

        TokensText.Text = $"Tokens: {Human(stats?.TotalTokens ?? 0)}";
        CostText.Text = stats?.Cost > 0 ? $"${stats.Cost:0.000}" : "";
    }

    private void OnCompactingStarted()
        => Dispatcher.UIThread.Post(() =>
        {
            AddSystem("Auto-compacting conversation history...");
            SetCompactStatus("Compacting...", AppDot.Working);
        });

    private void OnCompacted()
        => Dispatcher.UIThread.Post(() =>
        {
            AddSystem("Context auto-compacted (history summarized to free up space).");
            SetCompactStatus("Compacted", AppDot.Idle);
            _ = ResetCompactLabelAsync();
        });

    /// <summary>Human-readable auto-compact status for the chat log at session start.</summary>
    private string DescribeAutoCompact(JsonNode spec)
    {
        if (!_settings.AutoCompact || spec["condenser"] is not JsonObject cond)
            return "Auto-compact is off - click the context ring to compact manually.";

        var max = cond["max_size"]?.GetValue<int>() ?? 280;
        if (max >= AgentSpecProvider.AutoCompactDisabledSize)
            return "Auto-compact is off - click the context ring to compact manually.";

        return $"Auto-compact is on - conversation history will be summarized automatically "
               + $"when it grows past about {max} events. Click the context ring to summarize early.";
    }

    /// <summary>
    /// Transient compaction feedback. The dedicated strip label was removed, so we
    /// surface status on the context ring's tooltip instead.
    /// </summary>
    private void SetCompactStatus(string label, AppDot dot)
        => ToolTip.SetTip(ContextButton, label);

    private const string ContextRingTip =
        "Context usage - click to compact (summarize) the conversation now";

    private async Task ResetCompactLabelAsync()
    {
        await Task.Delay(2500);
        if (_client is not null)
            ToolTip.SetTip(ContextButton, ContextRingTip);
    }

    // ---- chat helpers -------------------------------------------------------

    private const int MaxChatItems = 600;

    // The reasoning block currently showing the animated "Assistant is thinking..."
    // header. Only the latest one animates; any new output settles it to "Thought".
    private ChatItem? _activeThought;

    private ChatItem AddItem(ChatRole role, string header, string text)
    {
        // Any new line means the previous reasoning block is no longer "in progress",
        // so stop its animated header before adding the new item.
        StopThinkingAnimation();

        var item = new ChatItem { Role = role, Header = header, Text = text };
        if (role == ChatRole.Thought)
        {
            item.IsThinking = true;
            _activeThought = item;
        }
        _chat.Add(item);
        // Trim oldest lines so marathon sessions do not grow the collection without bound.
        if (_chat.Count > MaxChatItems)
        {
            int drop = _chat.Count - MaxChatItems / 2;
            for (int i = 0; i < drop; i++)
                _chat.RemoveAt(0);
        }
        ScrollToBottom();
        return item;
    }

    /// <summary>Settle the active reasoning block from "thinking..." to a static "Thought".</summary>
    private void StopThinkingAnimation()
    {
        if (_activeThought is null) return;
        _activeThought.IsThinking = false;
        _activeThought = null;
    }

    private void AddUser(string text)
    {
        // Sending a message always snaps the view back to the conversation tail.
        _autoScroll = true;
        AddItem(ChatRole.User, "You", text);
    }
    private void AddSystem(string text) => AddItem(ChatRole.System, "", text);
    private void AddError(string text) => AddItem(ChatRole.Error, "Error", text);

    // True while the chat should follow new content (the user is parked at the
    // bottom). A user scrolling up clears it; returning to the bottom (or clicking
    // the jump button) sets it again. Mirrors DC6's chat behavior.
    private bool _autoScroll = true;

    // Pixel slack for "close enough to the bottom" so sub-pixel layout rounding or a
    // partially visible last line still counts as being at the bottom. Generous (matches
    // DC6) so "follow latest" stays on while the user is near the tail.
    private const double BottomSlack = 56;

    private bool _scrollReposted;

    /// <summary>
    /// Scroll the chat to the newest message while auto-following. The critical detail
    /// (mirrors DC6's TrimChatAndScrollToEnd): a streamed/added bubble has only had its
    /// text set - its new size is NOT measured yet, so a bare ScrollToEnd() targets the
    /// STALE extent and stops short, leaving the tail clipped under the input strip.
    /// Forcing UpdateLayout() first finishes the pending measure/arrange so ScrollToEnd
    /// reaches the TRUE bottom. UpdateLayout is deferred into a single coalesced Render
    /// post so rapid streaming tokens don't each trigger a synchronous layout.
    /// </summary>
    private void ScrollToBottom()
    {
        if (!_autoScroll) return;

        // Immediate best-effort so following feels responsive (may use pre-layout extent).
        ChatScroll.ScrollToEnd();

        // Coalesce the authoritative corrections: at most one pending chain at a time.
        if (_scrollReposted) return;
        _scrollReposted = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_autoScroll)
            {
                _scrollReposted = false;
                return;
            }
            ChatScroll.UpdateLayout();   // finish pending layout -> real content extent
            ChatScroll.ScrollToEnd();    // now scroll to the actual end

            // Some controls (wrapping TextBox, newly visible banners/log panel) can
            // settle one dispatcher tick after Render. One final low-priority pass keeps
            // the last bubble above the dock without tying scrolling to LayoutUpdated.
            Dispatcher.UIThread.Post(() =>
            {
                _scrollReposted = false;
                if (!_autoScroll) return;
                ChatScroll.UpdateLayout();
                ChatScroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        }, DispatcherPriority.Render);
    }

    /// <summary>True when the chat scroll offset is at (or within slack of) the end.</summary>
    private bool IsChatAtBottom()
        => ChatScroll.Offset.Y >= ChatScroll.Extent.Height - ChatScroll.Viewport.Height - BottomSlack;

    /// <summary>
    /// Detect when the user scrolls away from the bottom to disable following, and
    /// toggle the floating jump button. While following, the button stays hidden.
    /// </summary>
    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        bool atBottom = IsChatAtBottom();

        // Decide whether to keep following. Order matters:
        //   1. At the bottom -> follow (covers reaching the end by any means).
        //   2. Otherwise, treat it as the user scrolling away ONLY when the offset
        //      moved upward on its OWN - i.e. the extent and viewport did not change.
        //
        // This guard is the critical fix: the streamed bubble's text is frequently
        // REPLACED with shorter text (markup/reasoning is stripped live, and the final
        // message swaps the draft for the authoritative answer). Shorter content shrinks
        // the extent, so the ScrollViewer clamps the offset DOWN and reports a negative
        // OffsetDelta. Without the ExtentDelta/ViewportDelta guard that clamp looked
        // exactly like a user scroll-up, which switched auto-follow OFF for the rest of
        // the turn and left every later line clipped under the input strip. A genuine
        // wheel/drag changes only the offset, so it still disables following correctly.
        if (atBottom)
            _autoScroll = true;
        else if (e.OffsetDelta.Y < 0 && e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)
            _autoScroll = false;

        if (ScrollDownButton is not null)
            ScrollDownButton.IsVisible = !_autoScroll && !atBottom;
    }

    /// <summary>Jump back to the latest message and resume auto-following.</summary>
    private void OnScrollDownClick(object? sender, RoutedEventArgs e)
    {
        _autoScroll = true;
        ChatScroll.ScrollToEnd();
        ScrollDownButton.IsVisible = false;
    }

    // Keep the in-memory log view bounded so long sessions don't grow it without
    // limit (the full log is still persisted to disk by BackendManager).
    private const int MaxLogChars = 120_000;

    private void OnBackendLog(string line)
        => Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            if (LogBox.Text.Length > MaxLogChars)
                LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
            LogBox.CaretIndex = LogBox.Text.Length;

            DetectStuckLoop(line);
            TryUpdatePrefill(line);
            TryUpdateLiveStats(line);
        });

    // Matches the engine's prompt-processing (prefill) progress lines, e.g.:
    //   "slot ... prompt processing, n_tokens = 6144, progress = 0.25, t = ..."
    // progress is the fraction (0..1) of the whole prompt read into the model
    // before generation starts. On big contexts this phase can take minutes.
    private static readonly Regex PrefillProgressRegex = new(
        @"prompt processing.*progress\s*=\s*([0-9]*\.?[0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // One-shot auto-hide: if no new progress line arrives within this window, the
    // prefill phase is done (generation started, finished, or errored), so hide the
    // bar. Each progress line restarts the timer. Avoids needing a dedicated "done"
    // signal that the engine does not always emit on the same line.
    private DispatcherTimer? _prefillHideTimer;

    /// <summary>
    /// Parse an engine log line for prompt-processing (prefill) progress and drive
    /// the small progress bar next to the token counter. Cheap no-op for the vast
    /// majority of lines that are not progress reports.
    /// </summary>
    private void TryUpdatePrefill(string line)
    {
        // Fast reject before the regex to keep the common path allocation-free.
        if (line.IndexOf("prompt processing", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var m = PrefillProgressRegex.Match(line);
        if (!m.Success
            || !double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var frac))
            return;

        var pct = Math.Clamp(frac, 0, 1) * 100.0;
        PrefillBar.Value = pct;
        PrefillText.Text = $"{pct:0}%";
        PrefillPanel.IsVisible = true;

        // (Re)arm the auto-hide. When prefill finishes there is no more progress
        // chatter, so the timer fires and clears the bar.
        _prefillHideTimer ??= CreatePrefillHideTimer();
        _prefillHideTimer.Stop();
        _prefillHideTimer.Start();
    }

    private DispatcherTimer CreatePrefillHideTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) => HidePrefillProgress();
        return t;
    }

    /// <summary>Hide and reset the prefill progress bar (turn end / phase done).</summary>
    private void HidePrefillProgress()
    {
        _prefillHideTimer?.Stop();
        if (PrefillPanel is null) return;
        PrefillPanel.IsVisible = false;
        PrefillBar.Value = 0;
        PrefillText.Text = "0%";
    }

    // ---- Live engine telemetry (single-line, reliable engine log fields) --------
    //
    // The agent-server only reports token/context usage to the GUI meter via its
    // websocket stats event, which can lag a turn. The engine, however, prints
    // precise single-line numbers as it works, and those lines already flow through
    // OnBackendLog. We parse them to drive the context ring and a live generation
    // readout in real time, so the GUI reflects what the user already sees in the
    // log. Websocket stats (_lastStats) still refine the totals afterwards.

    // "slot ... new prompt, n_ctx_slot = 32768, n_keep = 4, task.n_tokens = 140"
    private static readonly Regex NewPromptRegex = new(
        @"new prompt.*?task\.n_tokens\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "n_ctx_slot = 32768" - the engine's real runtime context window.
    private static readonly Regex CtxSlotRegex = new(
        @"n_ctx_slot\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "slot print_timing: ... n_decoded = 192, tg = 37.16 t/s" - generated tokens
    // so far this turn and the current decode speed.
    private static readonly Regex DecodeRegex = new(
        @"n_decoded\s*=\s*(\d+).*?tg\s*=\s*([0-9]*\.?[0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Live per-turn token count derived from the engine (prompt + generated). When
    // set it takes precedence over the websocket snapshot so the ring moves live.
    private long? _liveTurnTokens;
    // Prompt (prefill) size of the active turn; generated tokens are added to it.
    private long _turnPromptTokens;
    // Auto-hide for the generation readout once decode chatter stops.
    private DispatcherTimer? _genHideTimer;

    /// <summary>
    /// Parse a single engine log line for live context/generation numbers and update
    /// the meter and the generation readout. Cheap fast-rejects keep the common
    /// (non-matching) line allocation-free.
    /// </summary>
    private void TryUpdateLiveStats(string line)
    {
        // True runtime context window straight from the engine slot.
        if (line.IndexOf("n_ctx_slot", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var cm = CtxSlotRegex.Match(line);
            if (cm.Success && long.TryParse(cm.Groups[1].Value, out var ctx) && ctx > 0
                && ctx != _assumedContextWindow)
            {
                _assumedContextWindow = ctx;
                RefreshStatsDisplay();
            }
        }

        // New turn: capture the prompt size and start the live count there.
        if (line.IndexOf("new prompt", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var pm = NewPromptRegex.Match(line);
            if (pm.Success && long.TryParse(pm.Groups[1].Value, out var prompt))
            {
                _turnPromptTokens = prompt;
                _liveTurnTokens = prompt;
                RefreshStatsDisplay();
            }
            return;
        }

        // Generation progress: tokens decoded so far + current speed.
        if (line.IndexOf("n_decoded", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var dm = DecodeRegex.Match(line);
            if (dm.Success
                && long.TryParse(dm.Groups[1].Value, out var decoded)
                && double.TryParse(dm.Groups[2].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var tps))
            {
                _liveTurnTokens = _turnPromptTokens + decoded;
                GenText.Text = $"{decoded} tok @ {tps:0.0} t/s";
                GenText.IsVisible = true;
                RefreshStatsDisplay();

                _genHideTimer ??= CreateGenHideTimer();
                _genHideTimer.Stop();
                _genHideTimer.Start();
            }
            return;
        }

        // Turn finished: stop the live speed readout (keep the ring where it landed;
        // the websocket stats event will reconcile the final totals).
        if (line.IndexOf("stop processing", StringComparison.OrdinalIgnoreCase) >= 0)
            HideGenStats();
    }

    private DispatcherTimer CreateGenHideTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        t.Tick += (_, _) => HideGenStats();
        return t;
    }

    /// <summary>Hide the live generation readout (decode finished / went quiet).</summary>
    private void HideGenStats()
    {
        _genHideTimer?.Stop();
        if (GenText is null) return;
        GenText.IsVisible = false;
        GenText.Text = "";
    }

    // Set once per turn so the "got stuck" explanation is shown at most once even
    // though the backend emits several related warning lines. Reset on each new turn.
    private bool _stuckNotifiedThisTurn;

    /// <summary>
    /// Watch the backend log for the agent-server's stuck/empty-response signals. When
    /// a local model keeps returning empty responses or repeats the same step, the
    /// server's stuck detector halts the run - which otherwise looks to the user like
    /// the agent silently stopped after saying "I'll do X". Surface a clear, honest
    /// explanation (and what to try) instead of a silent stop.
    /// </summary>
    private void DetectStuckLoop(string line)
    {
        if (_stuckNotifiedThisTurn) return;

        bool stuck = line.Contains("Stuck pattern detected", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("Action, Observation loop detected", StringComparison.OrdinalIgnoreCase);
        if (!stuck) return;

        _stuckNotifiedThisTurn = true;
        AddSystem(
            "The agent got stuck - the model kept returning empty responses or repeating the "
            + "same step, so StayVibin stopped the run to avoid an endless loop. This is the "
            + "model struggling with the task, not a crash. Try: rephrase or simplify the "
            + "request, break it into smaller steps, or switch to a stronger tool-capable model "
            + "in the Model Store. You can also just press Send again to retry.");
    }

    /// <summary>Toggle the collapsible server log from the sidebar Log button.</summary>
    private void OnToggleLog(object sender, RoutedEventArgs e)
    {
        var show = !LogPanel.IsVisible;
        LogPanel.IsVisible = show;
        // Reflect the open/closed state on the sidebar button.
        if (show) LogButton.Classes.Add("active");
        else LogButton.Classes.Remove("active");
    }

    // ---- window state -------------------------------------------------------

    /// <summary>
    /// React to maximize/restore (and the size/position changes that come with it) so
    /// we can keep the docked input strip on screen.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty
            || change.Property == OffScreenMarginProperty
            || change.Property == BoundsProperty)
            UpdateMaximizeInset();
    }

    /// <summary>
    /// Keep the bottom input strip (message box, Send/Stop) on screen when maximized.
    /// When maximized the window can extend past the visible work area - by the OS
    /// resize-border overflow, and on some setups far enough that the docked strip
    /// lands behind the taskbar. Measure the real overflow of the window frame below
    /// the screen's work area and inset the root content by that much; the inset is
    /// zero in the normal/restored state. We over-correct slightly (frame vs client
    /// bottom) which only adds a few invisible pixels of off-screen margin.
    /// </summary>
    private void UpdateMaximizeInset()
    {
        if (WindowState is not (WindowState.Maximized or WindowState.FullScreen))
        {
            RootGrid.Margin = default;
            return;
        }

        // Start from Avalonia's reported off-screen margin (the resize-border overflow).
        double bottomInset = OffScreenMargin.Bottom;

        // Then measure the frame's actual overflow below the work area, which also
        // captures the case where the maximized window covers the taskbar.
        var screen = Screens.ScreenFromWindow(this);
        if (screen is not null && FrameSize is { } frame)
        {
            double scaling = screen.Scaling > 0 ? screen.Scaling : RenderScaling;
            double frameBottomPx = Position.Y + frame.Height * scaling;
            double workBottomPx = screen.WorkingArea.Y + screen.WorkingArea.Height;
            double overflowDip = (frameBottomPx - workBottomPx) / scaling;
            if (overflowDip > bottomInset) bottomInset = overflowDip;
        }

        RootGrid.Margin = new Thickness(0, 0, 0, Math.Max(0, bottomInset));
    }

    // ---- status / state -----------------------------------------------------

    private enum AppDot { Down, Connecting, Idle, Working }

    private void SetStatus(string text, AppDot dot)
    {
        StatusText.Text = text;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        // Gate the model dropdown so a switch can't start a second conversation
        // recreate while a Start/Stop/switch is already in flight (which would leak
        // the in-progress client and its event subscriptions).
        ModelCombo.IsEnabled = !busy;
    }

    /// <summary>Toggle whole-session UI (Start vs Stop, input availability, stats).</summary>
    private void SetSessionActive(bool active)
    {
        if (active)
        {
            StartButtonText.Text = "Stop";
            StartButton.Classes.Remove("accent");
            StartButton.Classes.Add("danger");
            StartButton.IsEnabled = true;
            WorkDirButton.IsEnabled = false;
            InputBox.IsEnabled = true;
            ContextButton.IsEnabled = true;
            AttachButton.IsEnabled = true;
            SetRunning(false);
        }
        else
        {
            StartButtonText.Text = "Start";
            StartButton.Classes.Remove("danger");
            StartButton.Classes.Add("accent");
            StartButton.IsEnabled = true;
            WorkDirButton.IsEnabled = true;
            InputBox.IsEnabled = false;
            ContextButton.IsEnabled = false;
            AttachButton.IsEnabled = false;
            InterruptButton.IsEnabled = false;
            SteerButton.IsEnabled = false;
            SendButton.IsEnabled = false;
            SendButton.Content = "Send";
            ClearAttachments();
            _queue.Clear();
            UpdateQueueIndicator();
            _agentRunning = false;
            _planAwaitingApproval = false;
            SetPlanApprovalVisible(false);
            ClearPermissionApproval();
            SetActivity(false);
            ResetStatsDisplay();
        }
    }

    private void SetRunning(bool running)
    {
        _agentRunning = running;
        // When the agent stops working, a trailing reasoning block is done thinking.
        if (!running) StopThinkingAnimation();
        bool session = _client is not null;
        InterruptButton.IsEnabled = running;     // "Stop" - halts the current task
        SteerButton.IsEnabled = running;         // interject immediately
        // Send stays available while running so a message can be queued; its label
        // reflects whether it will send now or wait for the current task to finish.
        SendButton.IsEnabled = session;
        SendButton.Content = running ? "Queue" : "Send";
        InputBox.IsEnabled = session;
        SetActivity(running);
    }

    private bool _activityOn;

    /// <summary>
    /// Show/animate the "agent working" cues: the spinner inside the Start/Stop
    /// button and the pulsing status dot. Guarded so repeated status ticks don't
    /// restart the animations.
    /// </summary>
    private void SetActivity(bool on)
    {
        if (on == _activityOn) return;
        _activityOn = on;

        if (on)
        {
            ButtonSpinner.IsVisible = true;
        }
        else
        {
            ButtonSpinner.IsVisible = false;
        }
    }

    private void ResetStatsDisplay()
    {
        _lastStats = null;
        _liveTurnTokens = null;
        _turnPromptTokens = 0;
        ContextArc.SweepAngle = 0;
        ContextText.Text = "-- / --";
        TokensText.Text = "Tokens: 0";
        CostText.Text = "";
        ToolTip.SetTip(ContextButton, ContextRingTip);
        HideGenStats();
        HidePrefillProgress();
    }

    // ---- file explorer ------------------------------------------------------

    private List<FileNode> _treeRoots = new();
    private IReadOnlyDictionary<string, char> _statusMap = new Dictionary<string, char>();
    private int _treeSeq;   // drop stale tree refreshes when folder changes quickly

    /// <summary>Rescan the working folder and git status into the explorer tree.</summary>
    private async Task RefreshTreeAsync()
    {
        var seq = ++_treeSeq;

        if (string.IsNullOrWhiteSpace(_workingDir) || !Directory.Exists(_workingDir))
        {
            if (seq == _treeSeq) FileTree.ItemsSource = null;
            return;
        }

        var map = await GitService.GetStatusMapAsync(_workingDir);
        if (seq != _treeSeq) return;

        _statusMap = map;
        _treeRoots = WorkspaceExplorer.Load(_workingDir, _statusMap);
        FileTree.ItemsSource = _treeRoots;

        // If the open file changed on disk (e.g. the agent edited it) and we have
        // no unsaved edits, refresh the editor to match.
        ReloadEditorIfChangedOnDisk();
    }

    private void OnRefreshTree(object sender, RoutedEventArgs e) => _ = RefreshTreeAsync();

    /// <summary>Lazily fill a folder's children the first time it is expanded.</summary>
    private void OnTreeItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TreeViewItem { DataContext: FileNode node } || !node.IsDirectory)
            return;

        if (node.Children.Count == 1 && node.Children[0].IsPlaceholder)
        {
            node.Children.Clear();
            foreach (var child in WorkspaceExplorer.Load(node.FullPath, _statusMap))
                node.Children.Add(child);
        }
    }

    private void OnTreeDoubleClick(object sender, TappedEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode { IsDirectory: false } node)
            OpenFileInEditor(node.FullPath);
    }

    /// <summary>Show or hide the explorer column.</summary>
    private void OnToggleExplorer(object sender, RoutedEventArgs e)
    {
        ExplorerPanel.IsVisible = !ExplorerPanel.IsVisible;
        ExplorerSplitter.IsVisible = ExplorerPanel.IsVisible;
    }

    // ---- code editor --------------------------------------------------------

    private string? _editorPath;
    private bool _editorDirty;
    private bool _loadingEditor;          // suppress dirty-tracking while loading text
    private DateTime _editorDiskWrite;    // last-write stamp of the loaded file

    private const long MaxEditorBytes = 2_000_000;

    /// <summary>Load a file into the editor (read-only of binaries/huge files is refused).</summary>
    private void OpenFileInEditor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxEditorBytes)
            {
                AddSystem($"{Path.GetFileName(path)} is too large to open here ({info.Length / 1024:N0} KB).");
                return;
            }

            var bytes = File.ReadAllBytes(path);
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                AddSystem($"{Path.GetFileName(path)} looks like a binary file; not opening it in the editor.");
                return;
            }

            _loadingEditor = true;
            try
            {
                CodeEditor.Text = System.Text.Encoding.UTF8.GetString(bytes);
                CodeEditor.SyntaxHighlighting =
                    HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            }
            finally
            {
                _loadingEditor = false;
            }

            _editorPath = path;
            _editorDiskWrite = info.LastWriteTimeUtc;
            _editorDirty = false;
            EditorSaveButton.IsEnabled = false;
            EditorFileName.Text = Path.GetFileName(path);
            ToolTip.SetTip(EditorFileName, path);
            ShowEditor(true);
        }
        catch (Exception ex)
        {
            _loadingEditor = false;
            AddError($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Toggle the editor panel open/closed and its splitter.</summary>
    private void ShowEditor(bool show)
    {
        EditorPanel.IsVisible = show;
        EditorSplitter.IsVisible = show;
        if (!show)
        {
            _editorPath = null;
            _editorDirty = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_loadingEditor || _editorPath is null || _editorDirty) return;
        _editorDirty = true;
        EditorSaveButton.IsEnabled = true;
        EditorFileName.Text = Path.GetFileName(_editorPath) + " *";
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            SaveEditor();
        }
    }

    private void OnEditorSave(object sender, RoutedEventArgs e) => SaveEditor();

    private void SaveEditor()
    {
        if (_editorPath is null || !_editorDirty) return;
        try
        {
            File.WriteAllText(_editorPath, CodeEditor.Text);
            _editorDirty = false;
            _editorDiskWrite = File.GetLastWriteTimeUtc(_editorPath);
            EditorSaveButton.IsEnabled = false;
            EditorFileName.Text = Path.GetFileName(_editorPath);
            _ = RefreshTreeAsync();        // file is now modified per git
            _ = UpdateRepoBadgeAsync();
        }
        catch (Exception ex)
        {
            AddError($"Could not save {Path.GetFileName(_editorPath)}: {ex.Message}");
        }
    }

    private async void OnEditorClose(object sender, RoutedEventArgs e)
    {
        if (_editorDirty && _editorPath is not null)
        {
            var choice = await MessageBox.ShowAsync(
                this, $"Save changes to {Path.GetFileName(_editorPath)}?", "Unsaved changes",
                MessageBoxButton.YesNo);
            if (choice == MessageBoxResult.No) return;
            if (choice == MessageBoxResult.Yes) SaveEditor();
        }
        ShowEditor(false);
    }

    /// <summary>Reload the open file if it changed on disk and has no unsaved edits.</summary>
    private void ReloadEditorIfChangedOnDisk()
    {
        if (_editorPath is null || _editorDirty || !File.Exists(_editorPath)) return;
        try
        {
            var write = File.GetLastWriteTimeUtc(_editorPath);
            if (write <= _editorDiskWrite) return;

            var caret = CodeEditor.CaretOffset;
            _loadingEditor = true;
            try
            {
                CodeEditor.Text = File.ReadAllText(_editorPath);
            }
            finally
            {
                _loadingEditor = false;
            }
            CodeEditor.CaretOffset = Math.Min(caret, CodeEditor.Document.TextLength);
            _editorDiskWrite = write;
            _editorDirty = false;
            EditorSaveButton.IsEnabled = false;
            EditorFileName.Text = Path.GetFileName(_editorPath);
        }
        catch { /* leave the current buffer as-is on any read error */ }
    }

    // ---- teardown -----------------------------------------------------------

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;

        var client = _client;
        _client = null;   // detach first so nothing else uses it during teardown
        client.Update -= OnAgentUpdate;
        client.StatusChanged -= OnServerStatus;
        client.StatsUpdated -= OnStats;
        client.CompactingStarted -= OnCompactingStarted;
        client.Compacted -= OnCompacted;
        client.Disconnected -= OnDisconnected;

        // Dispose blocks up to ~1s closing the WebSocket; run it off the UI thread
        // so the app stays responsive while a session is torn down.
        await Task.Run(() => { try { client.Dispose(); } catch { } });
    }

    private void Cleanup()
    {
        if (_client is not null)
        {
            _client.Update -= OnAgentUpdate;
            _client.StatusChanged -= OnServerStatus;
            _client.StatsUpdated -= OnStats;
            _client.CompactingStarted -= OnCompactingStarted;
            _client.Compacted -= OnCompacted;
            _client.Disconnected -= OnDisconnected;
            try { _client.Dispose(); } catch { }
            _client = null;
        }
        _backend.LogLine -= OnBackendLog;
        try { _backend.Dispose(); } catch { }
        if (_engine is not null) _engine.LogLine -= OnBackendLog;
        try { _engine?.Dispose(); } catch { }
        try { _ollama?.Dispose(); } catch { }
        try { _flushLock.Dispose(); } catch { }
        try { _modelLoadLock.Dispose(); } catch { }
        // Cancel any in-flight model pull and release the store cancellation sources.
        try { _pullCts?.Cancel(); _pullCts?.Dispose(); } catch { }
        try { _storeCts.Cancel(); _storeCts.Dispose(); } catch { }
        // Stop the auto-hide UI timers; a ticking DispatcherTimer keeps the window
        // referenced (via the dispatcher's timer list) after it closes.
        try { _genHideTimer?.Stop(); } catch { }
        try { _prefillHideTimer?.Stop(); } catch { }
    }

    // ---- misc ---------------------------------------------------------------

    private static string StripProvider(string model)
        => model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
            ? model["openai/".Length..]
            : model;

    private static string ToModelField(string ollamaName)
        => ollamaName.Contains('/') ? ollamaName : "openai/" + ollamaName;

    private static void ApplyModel(JsonNode spec, string ollamaName)
    {
        var field = ToModelField(ollamaName);
        if (spec["llm"] is JsonObject l) l["model"] = field;
        if (spec["condenser"]?["llm"] is JsonObject c) c["model"] = field;
    }

    private static string ShortPath(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static string Human(long n)
        => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M"
         : n >= 1_000 ? $"{n / 1_000.0:0.0}k"
         : n.ToString();

    // =================== EMBEDDED TAB VIEWS & LOGIC ===================

    // ---- Sidebar Tab Navigation ----
    private void OnSidebarTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int index))
        {
            MainTabControl.SelectedIndex = index;

            // Toggle active classes on tab buttons
            TabChatBtn.Classes.Remove("active");
            TabStoreBtn.Classes.Remove("active");
            TabSettingsBtn.Classes.Remove("active");

            btn.Classes.Add("active");

            // Refresh the Store every time it is shown so the disk-space line, the
            // installed-size totals, and the "Installed" tags are current. The one-shot
            // refresh at window load runs before the engine is up, so without this the
            // free-space/usage figures stay stale until the user hits Refresh manually.
            if (index == 1 && !_storeBusy)
            {
                _ = RefreshStoreAsync();
            }

            // If switching to Settings, reload Settings state
            if (index == 2)
            {
                LoadSettingsToUi();
            }
        }
    }

    // ---- Settings Ported Logic ----
    private void LoadSettingsToUi()
    {
        try
        {
            // App settings
            HostBox.Text = _settings.Host;
            PortBox.Text = _settings.Port.ToString();
            OllamaBox.Text = _settings.OllamaUrl;
            ExePathBox.Text = _settings.AgentServerPath;
            ContextLenBox.Text = _settings.ContextLength > 0 ? _settings.ContextLength.ToString() : "";
            MaxIterBox.Text = _settings.MaxIterations.ToString();
            WorkDirBox.Text = _settings.DefaultWorkingDir;

            // VRAM & Engine settings
            SelectKvCacheType(_settings.KvCacheType);
            FlashAttentionBox.IsChecked = _settings.EnableFlashAttention;
            AutoCompactBox.IsChecked = _settings.AutoCompact;
            DeviceCpuRadio.IsChecked = _settings.ComputeDevice == ComputeDevice.Cpu;
            DeviceGpuRadio.IsChecked = _settings.ComputeDevice != ComputeDevice.Cpu;

            // Populate the default-model picker from the models actually installed in
            // Ollama (mirrors the top-bar model dropdown). Conversation titles are NOT
            // models, so they must never seed this list. The saved/configured model is
            // appended just below when it is not already present.
            ModelBox.Items.Clear();
            foreach (var entry in ModelCombo.Items.OfType<ModelEntry>())
            {
                if (!string.IsNullOrWhiteSpace(entry.Name) && !ModelBox.Items.Contains(entry.Name))
                    ModelBox.Items.Add(entry.Name);
            }

            _settingsSpec = AgentSpecProvider.LoadRaw();
            var llm = _settingsSpec["llm"];
            var model = StripProvider(llm?["model"]?.GetValue<string>() ?? "");
            
            if (!string.IsNullOrEmpty(model) && !ModelBox.Items.Contains(model))
                ModelBox.Items.Add(model);
                
            ModelBox.Text = model;
            ApiKeyBox.Text = llm?["api_key"]?.GetValue<string>() ?? "";
            BaseUrlBox.Text = llm?["base_url"]?.GetValue<string>() ?? "";
            TemperatureBox.Text = llm?["temperature"]?.GetValue<double>().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            SelectReasoning(llm?["reasoning_effort"]?.GetValue<string>());
            NonNativeBox.IsChecked = !(llm?["native_tool_calling"]?.GetValue<bool>() ?? false);
            CondenserBox.Text = (_settingsSpec["condenser"]?["max_size"]?.GetValue<int>() ?? 240).ToString();

            // Enable elements
            ModelBox.IsEnabled = true;
            ApiKeyBox.IsEnabled = true;
            BaseUrlBox.IsEnabled = true;
            TemperatureBox.IsEnabled = true;
            ReasoningBox.IsEnabled = true;
            CondenserBox.IsEnabled = true;
            NonNativeBox.IsEnabled = true;
        }
        catch
        {
            // No agent config yet
            ModelBox.IsEnabled = false;
            ApiKeyBox.IsEnabled = false;
            BaseUrlBox.IsEnabled = false;
            TemperatureBox.IsEnabled = false;
            ReasoningBox.IsEnabled = false;
            CondenserBox.IsEnabled = false;
            NonNativeBox.IsEnabled = false;
        }
    }

    private void SelectReasoning(string? value)
    {
        value ??= "high";
        foreach (var obj in ReasoningBox.Items)
            if (obj is ComboBoxItem it && (it.Content as string) == value)
            {
                ReasoningBox.SelectedItem = it;
                return;
            }
        ReasoningBox.SelectedIndex = 0;
    }

    private void SelectKvCacheType(string? value)
    {
        value = (value ?? "f16").Trim().ToLowerInvariant();
        foreach (var obj in KvCacheBox.Items)
            if (obj is ComboBoxItem it && (it.Tag as string) == value)
            {
                KvCacheBox.SelectedItem = it;
                return;
            }
        KvCacheBox.SelectedIndex = 0;
    }

    private async void OnSaveSettings(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            await MessageBox.ShowAsync(this, "Port must be a number between 1 and 65535.");
            return;
        }

        int ctx = 0;
        var ctxText = ContextLenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(ctxText))
            ctx = 0;
        else if (!int.TryParse(ctxText, out ctx) || ctx < 1024)
        {
            await MessageBox.ShowAsync(this, "Max context must be a number >= 1024, or blank for auto.");
            return;
        }

        if (!int.TryParse(MaxIterBox.Text, out var maxIter) || maxIter < 1)
        {
            await MessageBox.ShowAsync(this, "Max iterations must be a positive number.");
            return;
        }

        double? temperature = null;
        if (!string.IsNullOrWhiteSpace(TemperatureBox.Text))
        {
            if (!double.TryParse(TemperatureBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
            {
                await MessageBox.ShowAsync(this, "Temperature must be a number (or blank).");
                return;
            }
            temperature = t;
        }

        int condenser = 240;
        if (_settingsSpec is not null && (!int.TryParse(CondenserBox.Text, out condenser) || condenser < 10))
        {
            await MessageBox.ShowAsync(this, "Auto-compact threshold must be a number >= 10.");
            return;
        }

        // Save app settings
        var previousContext = _settings.ContextLength;
        _settings.Host = HostBox.Text?.Trim() ?? "";
        _settings.Port = port;
        _settings.OllamaUrl = OllamaBox.Text?.Trim() ?? "";
        _settings.AgentServerPath = ExePathBox.Text?.Trim() ?? "";
        _settings.ContextLength = ctx;
        var contextChanged = previousContext != _settings.ContextLength;
        _settings.MaxIterations = maxIter;
        _settings.DefaultWorkingDir = WorkDirBox.Text?.Trim() ?? "";
        _settings.KvCacheType = (KvCacheBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "f16";
        _settings.EnableFlashAttention = FlashAttentionBox.IsChecked == true;
        _settings.AutoCompact = AutoCompactBox.IsChecked == true;
        var previousDevice = _settings.ComputeDevice;
        _settings.ComputeDevice = DeviceCpuRadio.IsChecked == true ? ComputeDevice.Cpu : ComputeDevice.Gpu;
        var deviceChanged = previousDevice != _settings.ComputeDevice;
        _settings.Save();

        // Save model/agent config
        if (_settingsSpec is not null)
        {
            try
            {
                var model = ToModelField(ModelBox.Text?.Trim() ?? "");
                var apiKey = ApiKeyBox.Text?.Trim() ?? "";
                var baseUrl = BaseUrlBox.Text?.Trim() ?? "";
                var reasoning = (ReasoningBox.SelectedItem as ComboBoxItem)?.Content as string ?? "high";
                var nonNative = NonNativeBox.IsChecked == true;

                ApplyLlm(_settingsSpec["llm"], model, apiKey, baseUrl, temperature, reasoning, nonNative);
                ApplyLlm(_settingsSpec["condenser"]?["llm"], model, apiKey, baseUrl, temperature, reasoning, nonNative);
                if (_settingsSpec["condenser"] is JsonObject cond) cond["max_size"] = condenser;

                AgentSpecProvider.Save(_settingsSpec);
            }
            catch (Exception ex)
            {
                await MessageBox.ShowAsync(this, $"Could not save model settings: {ex.Message}");
                return;
            }
        }

        // Reload everything that settings can affect.
        _settings = AppSettings.Load();

        _ollama?.Dispose();
        _ollama = new OllamaClient(_settings.OllamaUrl);
        await RefreshAssumedContextWindowAsync();

        // A compute-device OR context change only takes effect when the engine process
        // is relaunched (accelerator visibility and OLLAMA_CONTEXT_LENGTH are both
        // fixed at startup). Rebuild the manager so the next Start re-evaluates with
        // the new values instead of reusing the stale (already-built) manager at the
        // old context - which is why a changed "Max context" previously never applied.
        if (deviceChanged || contextChanged) RebuildEngineManager();

        var deviceNote = deviceChanged
            ? (_settings.ComputeDevice == ComputeDevice.Cpu
                ? "\n\nCompute device set to CPU only - the engine will restart on the next Start (expect slower responses)."
                : "\n\nCompute device set to GPU - the engine will restart on the next Start.")
            : "";
        await MessageBox.ShowAsync(this, "Settings and VRAM optimizations saved and applied successfully!" + deviceNote);
    }

    private static void ApplyLlm(JsonNode? llm, string model, string apiKey, string baseUrl,
        double? temperature, string reasoning, bool nonNative)
    {
        if (llm is not JsonObject o) return;
        if (!string.IsNullOrEmpty(model)) o["model"] = model;
        o["api_key"] = string.IsNullOrEmpty(apiKey) ? "local-llm" : apiKey;
        if (!string.IsNullOrEmpty(baseUrl)) o["base_url"] = baseUrl;
        o["temperature"] = temperature is null ? null : JsonValue.Create(temperature.Value);
        o["reasoning_effort"] = reasoning;
        o["native_tool_calling"] = !nonNative;
    }

    private async void OnBrowseExe(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select agent-server.exe",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } } }
        });
        if (files.Count > 0)
        {
            ExePathBox.Text = files[0].Path.LocalPath;
        }
    }

    // ---- Model Store Ported Logic ----
    private async Task RefreshStoreAsync()
    {
        try
        {
            if (_ollama is null) _ollama = new OllamaClient(_settings.OllamaUrl);
            var installed = await _ollama.ListInstalledAsync(_storeCts.Token);

            var installedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            foreach (var m in installed)
            {
                totalBytes += m.SizeBytes;
                installedNames.Add(m.Name);
            }

            DiskSummary.Text = BuildDiskSummary(totalBytes);

            _unfilteredCatalog.Clear();
            foreach (var e in ModelAdvisor.Catalog)
            {
                bool present = installedNames.Contains(e.Model)
                               || installedNames.Contains(e.Model + ":latest");
                _unfilteredCatalog.Add(new CatalogRow(e.Model, e.Tier, e.TierOrder, e.Category,
                                             e.Recommended, e.Accuracy, e.Note, present));
            }

            ApplyStoreFilter();
        }
        catch
        {
            DiskSummary.Text = "Ollama connection offline. Ensure Ollama is running.";
        }
    }

    private async Task InstallStoreAsync(string model)
    {
        model = (model ?? "").Trim();
        if (_storeBusy || string.IsNullOrWhiteSpace(model)) return;

        SetStoreBusy(true);
        _pullCts = CancellationTokenSource.CreateLinkedTokenSource(_storeCts.Token);
        StopInstallButton.IsVisible = true;
        StopInstallButton.IsEnabled = true;
        ProgressText.Text = $"Preparing to install {model}...";
        ProgressBarCtl.Value = 0;
        try
        {
            int lastPercent = -1;
            string lastStatus = "";
            var progress = new Progress<PullProgress>(p =>
            {
                int pct = (int)p.Percent;
                if (pct == lastPercent && p.Status == lastStatus) return;
                lastPercent = pct;
                lastStatus = p.Status;

                ProgressBarCtl.Value = p.Percent;
                ProgressText.Text = p.Total > 0
                    ? $"Installing {model}: {p.Status} ({FormatBytes(p.Completed)} / {FormatBytes(p.Total)}, {pct}%)"
                    : $"Installing {model}: {p.Status}";
            });

            bool ok = await _ollama!.PullModelAsync(model, progress, _pullCts.Token);
            if (ok)
            {
                _ollama.ClearCache();
                InstallNameBox.Clear();
                await RefreshStoreAsync();
                await PopulateModelsAsync();
            }
            else
            {
                await MessageBox.ShowAsync(this, $"Install of '{model}' did not complete. Check the model name and try again.", "Model Store");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, $"Could not install '{model}'.\n\n{ex.Message}", "Model Store");
        }
        finally
        {
            StopInstallButton.IsVisible = false;
            _pullCts?.Dispose();
            _pullCts = null;
            SetStoreBusy(false);
        }
    }

    private async Task DeleteStoreAsync(string model)
    {
        if (_storeBusy) return;

        var confirm = await MessageBox.ShowAsync(this,
            $"Remove '{model}' from disk? You can reinstall it later.",
            "Remove model", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        SetStoreBusy(true);
        ProgressText.Text = $"Removing {model}...";
        ProgressBarCtl.IsIndeterminate = true;
        try
        {
            bool ok = await _ollama!.DeleteModelAsync(model, _storeCts.Token);
            if (ok)
            {
                _ollama.ClearCache();
                await RefreshStoreAsync();
                await PopulateModelsAsync();
            }
            else
            {
                await MessageBox.ShowAsync(this, $"Could not remove '{model}'.", "Model Store");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await MessageBox.ShowAsync(this, $"Could not remove '{model}'.\n\n{ex.Message}", "Model Store");
        }
        finally
        {
            ProgressBarCtl.IsIndeterminate = false;
            SetStoreBusy(false);
        }
    }

    private void SetStoreBusy(bool busy)
    {
        _storeBusy = busy;
        ProgressPanel.IsVisible = busy;
        if (!busy) ProgressBarCtl.IsIndeterminate = false;

        InstallNameBox.IsEnabled = !busy;
        InstallByNameButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        CategoryFilter.IsEnabled = !busy;
        AccuracyFilter.IsEnabled = !busy;
    }

    private void OnModelActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is CatalogRow row)
        {
            if (row.IsInstalled)
                _ = DeleteStoreAsync(row.Name);
            else
                _ = InstallStoreAsync(row.Name);
        }
    }

    private void OnStopInstall(object? sender, RoutedEventArgs e)
    {
        if (_pullCts is null) return;
        ProgressText.Text = "Stopping...";
        StopInstallButton.IsEnabled = false;
        _pullCts.Cancel();
    }

    private async void OnInstallByName(object? sender, RoutedEventArgs e)
        => await InstallStoreAsync(InstallNameBox.Text ?? "");

    private async void OnInstallNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await InstallStoreAsync(InstallNameBox.Text ?? "");
    }

    private void OnSearchBoxTextChanged(object? sender, TextChangedEventArgs e)
        => ApplyStoreFilter();

    private void OnFilterSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => ApplyStoreFilter();

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (_storeBusy) return;
        SetStoreBusy(true);
        try { _ollama?.ClearCache(); _ = RefreshStoreAsync(); }
        finally { SetStoreBusy(false); }
    }

    private void OnOpenGuide(object? sender, RoutedEventArgs e)
    {
        _ = MessageBox.ShowAsync(this, "VRAM Recommendations:\n\n"
            + "- 6GB VRAM: Try Mistral 7B or Qwen 2.5 7B with q4_0 quantization.\n"
            + "- 12GB VRAM: Try Llama 3 8B or Mistral 12B.\n"
            + "- 16GB+ VRAM: Try Qwen 2.5 14B or larger models.", "VRAM Model Guide");
    }

    /// <summary>
    /// Open the in-form "Installed Models" overlay (no separate window) and load the
    /// current list. Each row shows the model name, on-disk size, and a Remove button.
    /// </summary>
    private async void OnShowInstalledModels(object? sender, RoutedEventArgs e)
    {
        InstalledOverlay.IsVisible = true;
        await RefreshInstalledModelsPanelAsync();
    }

    /// <summary>Close the Installed Models overlay (ignored while a remove is in flight).</summary>
    private void OnCloseInstalledModels(object? sender, RoutedEventArgs e)
    {
        if (_installedPanelBusy) return;
        InstalledOverlay.IsVisible = false;
    }

    /// <summary>
    /// (Re)load the installed-model list into the overlay, updating the summary line,
    /// running total, and the empty/error status text. Best-effort: engine-unreachable
    /// and empty cases are surfaced inline rather than as popups.
    /// </summary>
    private async Task RefreshInstalledModelsPanelAsync()
    {
        if (_ollama is null) _ollama = new OllamaClient(_settings.OllamaUrl);

        IReadOnlyList<InstalledModel> installed;
        try
        {
            installed = await _ollama.ListInstalledAsync(_storeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // app/store shutting down; nothing to show
        }
        catch
        {
            _installedModels.Clear();
            InstalledOverlaySummary.Text = "";
            InstalledOverlayTotal.Text = "";
            SetInstalledOverlayStatus(
                "Could not reach the StayVibin Engine. Press Start (or wait for the "
                + "engine to come up) and reopen this list.");
            return;
        }

        _installedModels.Clear();
        long total = 0;
        foreach (var m in installed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            total += m.SizeBytes;
            _installedModels.Add(new InstalledModelRow(m.Name, m.SizeBytes));
        }

        if (_installedModels.Count == 0)
        {
            InstalledOverlaySummary.Text = "";
            InstalledOverlayTotal.Text = "";
            SetInstalledOverlayStatus(
                "No models are installed yet. Use a Store card or \"Install by name\" "
                + "to download one.");
            return;
        }

        SetInstalledOverlayStatus(null);
        InstalledOverlaySummary.Text =
            $"{_installedModels.Count} {(_installedModels.Count == 1 ? "model" : "models")} installed";
        InstalledOverlayTotal.Text = $"Total on disk: {FormatBytes(total)}";
    }

    /// <summary>Show (or hide, when null) the overlay's inline status/empty/error line.</summary>
    private void SetInstalledOverlayStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            InstalledOverlayStatus.IsVisible = false;
            InstalledOverlayStatus.Text = "";
        }
        else
        {
            InstalledOverlayStatus.Text = text;
            InstalledOverlayStatus.IsVisible = true;
        }
    }

    /// <summary>
    /// Remove a single model from the Installed Models overlay after confirmation,
    /// then refresh the overlay, the store catalog, and the model picker. Guards
    /// against overlapping removes/closes via <see cref="_installedPanelBusy"/>.
    /// </summary>
    private async void OnRemoveInstalledModel(object? sender, RoutedEventArgs e)
    {
        if (_installedPanelBusy) return;
        if (sender is not Button { DataContext: InstalledModelRow row }) return;

        var confirm = await MessageBox.ShowAsync(this,
            $"Remove '{row.Name}' from disk? You can reinstall it later.",
            "Remove model", MessageBoxButton.YesNo);
        if (confirm != MessageBoxResult.Yes) return;

        _installedPanelBusy = true;
        SetInstalledOverlayStatus($"Removing {row.Name}...");
        try
        {
            if (_ollama is null) _ollama = new OllamaClient(_settings.OllamaUrl);
            bool ok = await _ollama.DeleteModelAsync(row.Name, _storeCts.Token);
            if (!ok)
            {
                SetInstalledOverlayStatus($"Could not remove '{row.Name}'. Try again.");
                return;
            }

            // Removal succeeded: invalidate caches and refresh everything that lists
            // models so the catalog tags, picker, and this overlay all stay in sync.
            _ollama.ClearCache();
            await RefreshStoreAsync();
            await PopulateModelsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetInstalledOverlayStatus($"Could not remove '{row.Name}': {ex.Message}");
            return;
        }
        finally
        {
            _installedPanelBusy = false;
        }

        // Re-read the list outside the busy guard so the just-removed row disappears
        // and the totals update.
        await RefreshInstalledModelsPanelAsync();
    }

    private readonly List<CatalogRow> _unfilteredCatalog = new();

    private void ApplyStoreFilter()
    {
        if (CategoryFilter is null || AccuracyFilter is null || SearchBox is null)
            return;

        var categoryItem = CategoryFilter.SelectedItem as ComboBoxItem;
        var category = categoryItem?.Content as string;
        
        var accuracyItem = AccuracyFilter.SelectedItem as ComboBoxItem;
        var accuracyText = accuracyItem?.Content as string;

        var searchText = SearchBox.Text?.Trim();

        var matches = _unfilteredCatalog.Where(row =>
        {
            if (!string.IsNullOrEmpty(category) && category != "All categories"
                && !string.Equals(category, row.Category, StringComparison.OrdinalIgnoreCase))
                return false;

            var minAccuracy = accuracyText switch
            {
                "Tier 1: Max Accuracy" => ModelAdvisor.AccuracyTier.VeryGood,
                "Tier 2: Strong Accuracy" => ModelAdvisor.AccuracyTier.Good,
                "Tier 3: Moderate Accuracy" => ModelAdvisor.AccuracyTier.Fair,
                "Specialized Tools Only" => ModelAdvisor.AccuracyTier.Basic,
                _ => (ModelAdvisor.AccuracyTier?)null,
            };
            if (minAccuracy is { } min && row.Accuracy < min) return false;

            if (!string.IsNullOrEmpty(searchText)
                && row.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0
                && row.Note.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return true;
        })
        // Group installed models to the top so they are easy to find, then the
        // recommended picks, then by accuracy and VRAM tier within each group.
        .OrderByDescending(row => row.IsInstalled)
        .ThenByDescending(row => row.Recommended)
        .ThenByDescending(row => row.Accuracy)
        .ThenBy(row => row.TierOrder)
        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        _availableStoreModels.Clear();
        foreach (var m in matches)
        {
            _availableStoreModels.Add(m);
        }
    }

    private static string BuildDiskSummary(long totalBytes)
    {
        var used = $"Models use {FormatBytes(totalBytes)}";
        try
        {
            var dir = ModelsDirectory();
            var root = Path.GetPathRoot(dir);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    return $"{used} - {FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name}";
            }
        }
        catch { /* free-space lookup is best-effort */ }
        return used;
    }

    private static string ModelsDirectory()
    {
        var env = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) return env!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ollama", "models");
    }

    private static string FormatBytes(long bytes)
    {
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.0} GB";
        double mb = bytes / 1024d / 1024d;
        return $"{mb:0} MB";
    }
}

/// <summary>A catalog row; visibility helpers swap the Install button for an "Installed" label.</summary>
public sealed class CatalogRow
{
    public string Name { get; }
    public string Tier { get; }
    public int TierOrder { get; }
    public string Category { get; }
    public bool Recommended { get; }
    public ModelAdvisor.AccuracyTier Accuracy { get; }
    public string Note { get; }
    public bool IsInstalled { get; set; }

    public CatalogRow(string name, string tier, int tierOrder, string category,
                      bool recommended, ModelAdvisor.AccuracyTier accuracy,
                      string note, bool isInstalled)
    {
        Name = name;
        Tier = tier;
        TierOrder = tierOrder;
        Category = category;
        Recommended = recommended;
        Accuracy = accuracy;
        Note = note;
        IsInstalled = isInstalled;
    }

    public bool CanInstall => !IsInstalled;
    public string ActionText => IsInstalled ? "Remove" : "Install";
    public string AccuracyDisplay => ModelAdvisor.AccuracyLabel(Accuracy);
    public string VramDisplay => Tier;
    public string Description => Note;

    /// <summary>Tier-colored brush for the accuracy tag (green/amber/red), resolved
    /// from the shared app palette so it matches the rest of the UI.</summary>
    public IBrush AccuracyBrush =>
        Application.Current is { } app
        && app.TryFindResource(ModelAdvisor.AccuracyBrushKey(Accuracy), out var v)
        && v is IBrush b
            ? b
            : Brushes.Gray;
}

/// <summary>
/// One row in the in-form "Installed Models" overlay: an installed model's tag name
/// and its on-disk size. Immutable - the overlay rebuilds the list after any change.
/// </summary>
public sealed class InstalledModelRow
{
    public string Name { get; }
    public long SizeBytes { get; }

    public InstalledModelRow(string name, long sizeBytes)
    {
        Name = name;
        SizeBytes = sizeBytes;
    }

    /// <summary>Human-readable size (GB/MB) shown in the row's size chip.</summary>
    public string SizeDisplay
    {
        get
        {
            double gb = SizeBytes / 1024d / 1024d / 1024d;
            if (gb >= 1) return $"{gb:0.0} GB";
            double mb = SizeBytes / 1024d / 1024d;
            return $"{mb:0} MB";
        }
    }
}
