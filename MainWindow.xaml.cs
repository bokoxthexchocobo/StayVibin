using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
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

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chat;
        ConversationList.ItemsSource = _conversations;

        _backend = BuildBackend();
        _engine = BuildEngineManager();
        _workingDir = _settings.EffectiveWorkingDir;
        WorkDirButton.Content = ShortPath(_workingDir);
        WorkDirButton.ToolTip = _workingDir;

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
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnTreeItemExpanded));
        CodeEditor.TextChanged += OnEditorTextChanged;
        CodeEditor.PreviewKeyDown += OnEditorKeyDown;

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
            ModelSuggestionBar.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var model in models)
        {
            var btn = new Button
            {
                Content = "Install " + model,
                Style = (Style)FindResource("AccentButton"),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = $"Download {model} in the Model Store (no commands needed)",
                Tag = model,
            };
            btn.Click += OnInstallSuggestion;
            ModelSuggestionButtons.Children.Add(btn);
        }

        var store = new Button
        {
            Content = "Open Store",
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Browse and manage all models",
        };
        store.Click += async (_, _) => await OpenModelStoreAsync();
        ModelSuggestionButtons.Children.Add(store);

        var dismiss = new Button
        {
            Content = "Dismiss",
            Style = (Style)FindResource("FlatButton"),
            ToolTip = "Hide this suggestion",
        };
        dismiss.Click += (_, _) => HideModelSuggestions();
        ModelSuggestionButtons.Children.Add(dismiss);

        ModelSuggestionBar.Visibility = Visibility.Visible;
    }

    private void HideModelSuggestions()
    {
        ModelSuggestionBar.Visibility = Visibility.Collapsed;
        ModelSuggestionButtons.Children.Clear();
    }

    /// <summary>Install button in the suggestion bar: open the store and pull it.</summary>
    private async void OnInstallSuggestion(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model } || string.IsNullOrWhiteSpace(model))
            return;

        HideModelSuggestions();
        await OpenModelStoreAsync(model);
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;   // run once
        PromptInstallGitIfMissing();

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

        var dlg = new ProviderSetupWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true)
        {
            AddSystem("No AI provider configured yet. Press Start (or open Settings) to set one up.");
            return false;
        }

        // Keep the app's Ollama URL in sync with what the user entered.
        _settings.OllamaUrl = dlg.OllamaUrl;
        _settings.Save();
        _ollama?.Dispose();
        _ollama = new OllamaClient(_settings.OllamaUrl);

        try
        {
            AgentSpecProvider.CreateDefault(dlg.Model, dlg.OllamaUrl);
            var spec = AgentSpecProvider.Load();
            _llmTemplate = JsonNode.Parse(spec["llm"]!.ToJsonString());
            _selectedModel = StripProvider(AgentSpecProvider.DescribeModel(spec));
        }
        catch (Exception ex)
        {
            AddError($"Could not write the provider config: {ex.Message}");
            return false;
        }

        await PopulateModelsAsync();
        AddSystem($"Configured Ollama with model {dlg.Model}. Press Start to begin.");
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
            SetStatus("Starting StayVibin Engine...", AppDot.Connecting);
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

    private StayVibinEngineManager BuildEngineManager()
        => new(contextLength: _settings.BackendContextLength);

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

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // Don't let settings (which can dispose/rebuild the backend) run while a
        // Start/Stop is mid-flight.
        if (_busy) return;

        await EnsureOllamaRunningAsync();
        var models = _ollama is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : await _ollama.ListModelsAsync();

        var dlg = new SettingsWindow(_settings, models) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Reload everything that settings can affect.
        _settings = AppSettings.Load();

        _ollama?.Dispose();
        _ollama = new OllamaClient(_settings.OllamaUrl);
        // _assumedContextWindow is resolved per-model by RefreshAssumedContextWindowAsync below.

        if (_client is null)
        {
            // No active session: rebuild the backend so host/port/path/context apply,
            // rebuild the engine so context/URL apply, and refresh the default
            // working dir.
            RebuildBackend();
            try { _engine?.Dispose(); } catch { }
            _engine = BuildEngineManager();

            _workingDir = _settings.EffectiveWorkingDir;
            WorkDirButton.Content = ShortPath(_workingDir);
            WorkDirButton.ToolTip = _workingDir;
            _ = RefreshTreeAsync();

            if (AgentSpecProvider.SettingsExist)
            {
                try
                {
                    var spec = AgentSpecProvider.Load();
                    _llmTemplate = JsonNode.Parse(spec["llm"]!.ToJsonString());
                    _selectedModel = StripProvider(AgentSpecProvider.DescribeModel(spec));
                }
                catch { }
            }
            await PopulateModelsAsync();
            await RefreshAssumedContextWindowAsync();
            RefreshStatsDisplay();
            AddSystem("Settings saved.");
        }
        else
        {
            await PopulateModelsAsync();
            await RefreshAssumedContextWindowAsync();
            RefreshStatsDisplay();
            AddSystem("Settings saved. Connection/model defaults apply after you Stop and Start again.");
        }
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

    /// <summary>
    /// Open the Model Store (one-click install/remove of local models). Reuses the
    /// shared OllamaClient so cache state stays consistent. If the installed set
    /// changed, re-scan so the dropdown reflects additions/removals immediately.
    /// </summary>
    private async void OnModelStore(object sender, RoutedEventArgs e)
        => await OpenModelStoreAsync();

    /// <summary>
    /// Open the Model Store. When <paramref name="autoInstall"/> is set, the store
    /// starts downloading that model on open (used by the in-chat Install buttons so
    /// the user never has to touch a command line). Refreshes the model dropdown if
    /// anything was installed or removed.
    /// </summary>
    private async Task OpenModelStoreAsync(string? autoInstall = null)
    {
        if (_ollama is null) _ollama = new OllamaClient(_settings.OllamaUrl);

        var dlg = new ModelStoreWindow(_ollama, autoInstall) { Owner = this };
        dlg.ShowDialog();

        if (dlg.ModelsChanged)
            await PopulateModelsAsync();
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
            Foreground = (Brush)FindResource(warn ? "Warn" : dim ? "TextDim" : "Text")
        });

        var badge = new Border
        {
            Background = (Brush)FindResource("PanelAlt"),
            BorderBrush = (Brush)FindResource(warn ? "Warn" : "Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = row,
            ToolTip = tip
        };
        ToolTipService.SetInitialShowDelay(badge, 250);
        ToolTipService.SetShowDuration(badge, 20000);
        return badge;
    }

    // ---- UI event handlers --------------------------------------------------

    /// <summary>Let the user choose the folder the agent will operate in.</summary>
    private void OnPickWorkDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Choose working directory",
            InitialDirectory = _workingDir
        };
        if (dlg.ShowDialog(this) == true)
        {
            _workingDir = dlg.FolderName;
            WorkDirButton.Content = ShortPath(_workingDir);
            WorkDirButton.ToolTip = _workingDir;
            _ = UpdateRepoBadgeAsync();
            _ = RefreshTreeAsync();
        }
    }

    // ---- git / GitHub -------------------------------------------------------

    private const string GitDownloadUrl = "https://git-scm.com/download/win";

    /// <summary>
    /// If Git isn't installed, offer to send the user to the official download page.
    /// Git underpins the agent's version-control and GitHub features.
    /// </summary>
    private void PromptInstallGitIfMissing()
    {
        if (GitService.GitAvailable) return;

        var choice = MessageBox.Show(
            this,
            "Git is not installed on this PC.\n\n"
            + "StayVibin uses Git for version control and GitHub features "
            + "(commits, branches, pull requests). Would you like to download and "
            + "install it now?\n\nAfter installing, restart StayVibin.",
            "Install Git?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

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
            RepoButton.Visibility = Visibility.Collapsed;
            return;
        }

        var status = await GitService.GetStatusAsync(_workingDir);
        if (status is null)
        {
            RepoButton.Visibility = Visibility.Collapsed;
            return;
        }

        var dirtyMark = status.IsDirty ? " *" : "";
        RepoButton.Content = $"git: {status.Branch}{dirtyMark}";
        RepoButton.Foreground = status.IsDirty ? (Brush)FindResource("Warn") : (Brush)FindResource("TextDim");
        RepoButton.ToolTip = (status.RepoSlug is null ? "" : $"{status.RepoSlug}\n")
            + $"Branch: {status.Branch}\n"
            + (status.IsDirty ? $"{status.Dirty} uncommitted change(s)" : "Working tree clean")
            + "\n(click to refresh)";
        RepoButton.Visibility = Visibility.Visible;
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

        var choice = MessageBox.Show(
            this,
            "StayVibin's AI engine isn't installed yet.\n\n"
            + "StayVibin can set it up for you automatically (it installs uv if needed, "
            + "then downloads the engine). This needs an internet connection and can "
            + "take a few minutes.\n\nInstall it now?",
            "Set up StayVibin's AI engine?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
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
                         + "uv tool install openhands");
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

            JsonNode spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode);
            _llmTemplate ??= JsonNode.Parse(spec["llm"]!.ToJsonString());
            if (_selectedModel is not null) ApplyModel(spec, _selectedModel);

            if (_settings.AutoTune)
                await AutoTuneSpecAsync(spec);
            else
                // AutoTune off still needs correct tool-calling mode, otherwise the
                // saved spec's native_tool_calling=false breaks tool-capable models.
                await ApplyNativeToolCallingAsync(spec);
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
        => HistoryCol.Width = HistoryCol.Width.Value > 0 ? new GridLength(0) : new GridLength(220);

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

            var spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode);
            if (_selectedModel is not null) ApplyModel(spec, _selectedModel);
            if (_settings.AutoTune) await AutoTuneSpecAsync(spec);
            else await ApplyNativeToolCallingAsync(spec);

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
                WorkDirButton.ToolTip = _workingDir;
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

        var confirm = MessageBox.Show(this,
            "Delete this conversation permanently? This cannot be undone.",
            "Delete conversation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var ok = await AgentServerClient.DeleteConversationAsync(_backend.BaseUrl, id);
        if (!ok)
        {
            AddError("Could not delete the conversation (the server rejected the request).");
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
        _streamingItem = null;
        _lastAgentItem = null;
        _streamRaw.Clear();
        _streamStripMode = false;
        _streamEnvelopeMode = false;
        _lastAssistantText = "";
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

            var spec = await AgentSpecProvider.LoadAsync(_workingDir, _editorPath, planMode: _settings.PlanMode);
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
        // Ctrl+V with a screenshot/image on the clipboard -> attach it instead of
        // pasting nothing. Text paste falls through to the textbox as usual.
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && AttachButton.IsEnabled && Clipboard.ContainsImage() && !Clipboard.ContainsText())
        {
            var img = Clipboard.GetImage();
            if (img is not null)
            {
                StageClipboardImage(img);
                e.Handled = true;
                return;
            }
        }

        // Enter sends, or queues the message when the agent is busy; Shift+Enter =
        // newline. To interject immediately mid-task, use the Steer button.
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
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
    private void OnAttachClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Attach files, images or video",
            Multiselect = true,
            Filter = "All supported|*.*|"
                     + "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|"
                     + "Video|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.m4v|"
                     + "All files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        StagePaths(dlg.FileNames);
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
        e.Effects = AttachButton.IsEnabled && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        if (!AttachButton.IsEnabled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            StagePaths(files);
            e.Handled = true;
        }
    }

    /// <summary>Save a pasted/clipboard image to a temp PNG and stage it.</summary>
    private void StageClipboardImage(BitmapSource bmp)
    {
        try
        {
            var temp = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var fs = new System.IO.FileStream(temp, System.IO.FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                encoder.Save(fs);
            }
            _attachments.Add(AttachmentService.Stage(temp, _workingDir));
            RenderAttachChips();
        }
        catch (Exception ex)
        {
            AddError($"Could not paste image: {ex.Message}");
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
            AttachPanel.Visibility = Visibility.Collapsed;
            return;
        }
        AttachPanel.Visibility = Visibility.Visible;

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
                FontFamily = new FontFamily("Segoe UI Emoji"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = att.FileName,
                Foreground = (Brush)FindResource("Text"),
                FontSize = 12,
                Margin = new Thickness(5, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });

            var remove = new Button
            {
                Content = "\u2715",
                Foreground = (Brush)FindResource("TextDim"),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Remove"
            };
            var captured = att;
            remove.Click += (_, _) => { _attachments.Remove(captured); RenderAttachChips(); };
            row.Children.Add(remove);

            AttachPanel.Children.Add(new Border
            {
                Background = (Brush)FindResource("PanelAlt"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = row,
                ToolTip = att.DestRelPath
            });
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
        // ContextLength is the user-set ceiling; AutoTune fits each model under it.
        return (ModelTuning.Recommend(model, info, _settings.ContextLength), info);
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

        // The runtime window is what the meter should track. We do NOT overwrite
        // _settings.ContextLength here - that is the user's ceiling, not the result.
        _assumedContextWindow = rec.ContextLength;

        var kind = ModelTuning.IsThinkingModel(model, info) ? "thinking"
            : model.Contains("coder", StringComparison.OrdinalIgnoreCase) ? "coder"
            : "chat";
        AddSystem($"Auto-tuned {model} ({kind}): temperature {rec.Temperature:0.0}, "
                  + $"context {rec.ContextLength:N0} tokens, reasoning {rec.ReasoningEffort}.");
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

    private static void ApplyTuningToLlm(JsonNode? llm, TuneResult rec)
    {
        if (llm is not JsonObject o) return;
        o["temperature"] = rec.Temperature;
        o["reasoning_effort"] = rec.ReasoningEffort;
        o["max_input_tokens"] = rec.ContextLength;

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

    private void OnBodyMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Forward wheel events from the read-only message boxes to the chat scroller.
        if (e.Handled) return;
        e.Handled = true;
        ChatScroll.ScrollToVerticalOffset(ChatScroll.VerticalOffset - e.Delta);
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
        var text = InputBox.Text.Trim();
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
        var text = InputBox.Text.Trim();
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
        int n = _queue.Count;
        QueueText.Text = n == 0 ? "" : n == 1 ? "1 queued" : $"{n} queued";
        QueueText.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        => Dispatcher.Invoke(() => ApplyUpdate(u));

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
        => Dispatcher.Invoke(() =>
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
                if (falling)
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
        if (TryEnterPlanApproval())
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
        => PlanApprovalBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

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
        PermissionApprovalBar.Visibility = Visibility.Visible;
    }

    private void ClearPermissionApproval()
    {
        _permissionAwaitingApproval = false;
        PermissionApprovalBar.Visibility = Visibility.Collapsed;
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

        return LooksLikeStoppedPromise(text);
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

    private async Task AutoContinueWorkAsync()
    {
        if (_client is null) return;

        _turnAutoNudged = true;
        AddSystem("The assistant stopped after saying it would work, so StayVibin is continuing the turn automatically.");
        SetRunning(true);
        try
        {
            const string nudge =
                "Continue now and actually perform the work with your tools. Do not describe a plan "
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
        => Dispatcher.Invoke(() =>
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
        => Dispatcher.Invoke(() =>
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
        var used = stats?.PerTurnTokens ?? 0;
        var pct = window > 0 ? Math.Clamp(100.0 * used / window, 0, 100) : 0;

        ContextBar.Value = pct;
        ContextBar.Foreground = pct >= 90 ? (Brush)FindResource("Err")
            : pct >= 75 ? (Brush)FindResource("Warn")
            : (Brush)FindResource("Accent");
        ContextText.Text = $"{Human(used)} / {Human(window)} ({pct:0}%)";

        TokensText.Text = $"Tokens: {Human(stats?.TotalTokens ?? 0)}";
        CostText.Text = stats?.Cost > 0 ? $"${stats.Cost:0.000}" : "";
    }

    private void OnCompactingStarted()
        => Dispatcher.Invoke(() =>
        {
            AddSystem("Auto-compacting conversation history...");
            SetCompactStatus("Compacting...", AppDot.Working);
        });

    private void OnCompacted()
        => Dispatcher.Invoke(() =>
        {
            AddSystem("Context auto-compacted (history summarized to free up space).");
            SetCompactStatus("Compacted", AppDot.Idle);
            _ = ResetCompactLabelAsync();
        });

    /// <summary>Human-readable auto-compact status for the chat log at session start.</summary>
    private static string DescribeAutoCompact(JsonNode spec)
    {
        if (spec["condenser"] is not JsonObject cond)
            return "Auto-compact is off (no condenser configured in agent settings).";

        var max = cond["max_size"]?.GetValue<int>() ?? 280;
        return $"Auto-compact is on - conversation history will be summarized automatically "
               + $"when it grows past about {max} events. Use Compact now to summarize early.";
    }

    private void SetCompactStatus(string label, AppDot dot)
    {
        CompactText.Text = label;
        CompactText.Foreground = dot switch
        {
            AppDot.Working => (Brush)FindResource("Warn"),
            AppDot.Idle => (Brush)FindResource("Ok"),
            _ => (Brush)FindResource("TextDim")
        };
    }

    private async Task ResetCompactLabelAsync()
    {
        await Task.Delay(2500);
        if (_client is not null)
            SetCompactStatus("Auto-compact: on", AppDot.Idle);
    }

    // ---- chat helpers -------------------------------------------------------

    private const int MaxChatItems = 600;

    private ChatItem AddItem(ChatRole role, string header, string text)
    {
        var item = new ChatItem { Role = role, Header = header, Text = text };
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

    private void AddUser(string text) => AddItem(ChatRole.User, "You", text);
    private void AddSystem(string text) => AddItem(ChatRole.System, "", text);
    private void AddError(string text) => AddItem(ChatRole.Error, "Error", text);

    private void ScrollToBottom() => ChatScroll.ScrollToEnd();

    // Keep the in-memory log view bounded so long sessions don't grow it without
    // limit (the full log is still persisted to disk by BackendManager).
    private const int MaxLogChars = 120_000;

    private void OnBackendLog(string line)
        => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.Text.Length > MaxLogChars)
                LogBox.Text = LogBox.Text[^(MaxLogChars / 2)..];
            LogBox.ScrollToEnd();

            DetectStuckLoop(line);
        });

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

    // Height the bottom dock takes when the server log is expanded (remembered so
    // re-expanding restores whatever the user last dragged it to).
    private GridLength _logExpandedHeight = new(220);

    /// <summary>Expand the server log: give the bottom dock a resizable height.</summary>
    private void OnServerLogExpanded(object sender, RoutedEventArgs e)
    {
        BottomDockRow.Height = _logExpandedHeight;
        BottomDockRow.MinHeight = 110;
        BottomSplitter.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Collapse the server log: shrink the bottom dock to fit just the input row and
    /// the collapsed log header, so there is no empty gap at the window bottom.
    /// </summary>
    private void OnServerLogCollapsed(object sender, RoutedEventArgs e)
    {
        // Remember the dragged size so the next expand restores it.
        if (BottomDockRow.Height.IsAbsolute && BottomDockRow.Height.Value > 0)
            _logExpandedHeight = BottomDockRow.Height;
        BottomDockRow.Height = GridLength.Auto;
        BottomDockRow.MinHeight = 0;
        BottomSplitter.Visibility = Visibility.Collapsed;
    }

    // ---- status / state -----------------------------------------------------

    private enum AppDot { Down, Connecting, Idle, Working }

    private void SetStatus(string text, AppDot dot)
    {
        StatusText.Text = text;
        StatusDot.Fill = dot switch
        {
            AppDot.Idle => (Brush)FindResource("Ok"),
            AppDot.Working => (Brush)FindResource("Warn"),
            AppDot.Connecting => (Brush)FindResource("Warn"),
            _ => (Brush)FindResource("Err"),
        };
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
            StartButton.Style = (Style)FindResource("DangerButton");
            StartButton.IsEnabled = true;
            WorkDirButton.IsEnabled = false;
            InputBox.IsEnabled = true;
            CompactButton.IsEnabled = true;
            AttachButton.IsEnabled = true;
            SetRunning(false);
        }
        else
        {
            StartButtonText.Text = "Start";
            StartButton.Style = (Style)FindResource("AccentButton");
            StartButton.IsEnabled = true;
            WorkDirButton.IsEnabled = true;
            InputBox.IsEnabled = false;
            CompactButton.IsEnabled = false;
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

        var spin = (Storyboard)Resources["ButtonSpinSb"];
        var pulse = (Storyboard)Resources["DotPulseSb"];
        if (on)
        {
            ButtonSpinner.Visibility = Visibility.Visible;
            spin.Begin(this, true);
            pulse.Begin(this, true);
        }
        else
        {
            spin.Stop(this);
            pulse.Stop(this);
            ButtonSpinner.Visibility = Visibility.Collapsed;
            StatusDot.Opacity = 1.0;   // restore after the pulse leaves it dimmed
        }
    }

    private void ResetStatsDisplay()
    {
        _lastStats = null;
        ContextBar.Value = 0;
        ContextText.Text = "-- / --";
        TokensText.Text = "Tokens: 0";
        CostText.Text = "";
        CompactText.Text = "Auto-compact: on";
        CompactText.Foreground = (Brush)FindResource("Ok");
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
    private void OnTreeItemExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { DataContext: FileNode node } || !node.IsDirectory)
            return;

        if (node.Children.Count == 1 && node.Children[0].IsPlaceholder)
        {
            node.Children.Clear();
            foreach (var child in WorkspaceExplorer.Load(node.FullPath, _statusMap))
                node.Children.Add(child);
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode { IsDirectory: false } node)
            OpenFileInEditor(node.FullPath);
    }

    /// <summary>Show or hide the explorer column.</summary>
    private void OnToggleExplorer(object sender, RoutedEventArgs e)
        => ExplorerCol.Width = ExplorerCol.Width.Value > 0 ? new GridLength(0) : new GridLength(250);

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
            EditorTitle.Text = Path.GetFileName(path);
            EditorTitle.ToolTip = path;
            ShowEditor(true);
        }
        catch (Exception ex)
        {
            _loadingEditor = false;
            AddError($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Toggle the editor column open/closed and its splitter.</summary>
    private void ShowEditor(bool show)
    {
        if (show)
        {
            EditorCol.Width = new GridLength(1.4, GridUnitType.Star);
            EditorCol.MinWidth = 260;
            EditorSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            EditorCol.Width = new GridLength(0);
            EditorCol.MinWidth = 0;
            EditorSplitter.Visibility = Visibility.Collapsed;
            _editorPath = null;
            _editorDirty = false;
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_loadingEditor || _editorPath is null || _editorDirty) return;
        _editorDirty = true;
        EditorSaveButton.IsEnabled = true;
        EditorTitle.Text = Path.GetFileName(_editorPath) + " *";
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
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
            EditorTitle.Text = Path.GetFileName(_editorPath);
            _ = RefreshTreeAsync();        // file is now modified per git
            _ = UpdateRepoBadgeAsync();
        }
        catch (Exception ex)
        {
            AddError($"Could not save {Path.GetFileName(_editorPath)}: {ex.Message}");
        }
    }

    private void OnEditorClose(object sender, RoutedEventArgs e)
    {
        if (_editorDirty && _editorPath is not null)
        {
            var choice = MessageBox.Show(
                this, $"Save changes to {Path.GetFileName(_editorPath)}?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
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
            EditorTitle.Text = Path.GetFileName(_editorPath);
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
        try { _engine?.Dispose(); } catch { }
        try { _ollama?.Dispose(); } catch { }
        try { _flushLock.Dispose(); } catch { }
        try { _modelLoadLock.Dispose(); } catch { }
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
}
