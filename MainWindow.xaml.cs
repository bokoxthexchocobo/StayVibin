using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private AgentServerClient? _client;

    private string _workingDir;
    private bool _busy;             // a Start/Send operation is in flight
    private bool _agentRunning;     // the agent loop is actively running
    private ChatItem? _streamingItem;

    // Messages typed while the agent is busy wait here and are sent one at a time
    // as each agent turn finishes (queueing is the default; Steer interjects now).
    private readonly Queue<QueuedMessage> _queue = new();

    /// <summary>A user message waiting to be sent after the current turn finishes.</summary>
    private sealed record QueuedMessage(ChatItem Bubble, string Text, List<string> Images);

    private OllamaClient? _ollama;
    private JsonNode? _llmTemplate;     // detached clone of the configured LLM (for switches)
    private string? _selectedModel;     // ollama tag, e.g. "qwen2.5-coder:14b"
    private bool _populatingModels;

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chat;

        _backend = BuildBackend();
        _workingDir = _settings.EffectiveWorkingDir;
        WorkDirButton.Content = ShortPath(_workingDir);
        WorkDirButton.ToolTip = _workingDir;

        if (AgentSpecProvider.SettingsExist)
        {
            try
            {
                var spec = AgentSpecProvider.Load();
                _llmTemplate = JsonNode.Parse(spec["llm"]!.ToJsonString());
                _selectedModel = StripProvider(AgentSpecProvider.DescribeModel(spec));
            }
            catch { /* model picker still works once configured */ }
        }
        _ollama = new OllamaClient(_settings.OllamaUrl);

        AddSystem("Welcome. Pick a working folder, then press Start to launch the agent.");
        Closing += (_, _) => Cleanup();
        Loaded += OnWindowLoaded;

        // Explorer: populate children lazily on expand; double-click opens a file.
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(OnTreeItemExpanded));
        CodeEditor.TextChanged += OnEditorTextChanged;
        CodeEditor.PreviewKeyDown += OnEditorKeyDown;

        _ = PopulateModelsAsync();
        _ = UpdateRepoBadgeAsync();
        _ = RefreshTreeAsync();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;   // run once
        PromptInstallGitIfMissing();
    }

    private int _backendContext;

    private BackendManager BuildBackend()
    {
        _backendContext = _settings.ContextLength;
        var b = new BackendManager(
            _settings.Host, _settings.Port,
            _settings.EffectiveAgentServerPath, _settings.ContextLength);
        b.LogLine += OnBackendLog;
        return b;
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        // Don't let settings (which can dispose/rebuild the backend) run while a
        // Start/Stop is mid-flight.
        if (_busy) return;

        var models = _ollama is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : await _ollama.ListModelsAsync();

        var dlg = new SettingsWindow(_settings, models) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // Reload everything that settings can affect.
        _settings = AppSettings.Load();

        _ollama?.Dispose();
        _ollama = new OllamaClient(_settings.OllamaUrl);

        if (_client is null)
        {
            // No active session: rebuild the backend so host/port/path/context apply,
            // and refresh the default working dir.
            try { _backend.Dispose(); } catch { }
            _backend = BuildBackend();

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
            AddSystem("Settings saved.");
        }
        else
        {
            await PopulateModelsAsync();
            AddSystem("Settings saved. Connection/model defaults apply after you Stop and Start again.");
        }
    }

    /// <summary>
    /// Fill the model dropdown from Ollama, preserving the currently selected model
    /// even if it isn't installed locally, then refresh its capability strip.
    /// </summary>
    private async Task PopulateModelsAsync()
    {
        var models = _ollama is null
            ? Array.Empty<string>()
            : await _ollama.ListModelsAsync();

        _populatingModels = true;
        ModelCombo.Items.Clear();

        var list = new List<string>(models);
        if (_selectedModel is not null &&
            !list.Contains(_selectedModel, StringComparer.OrdinalIgnoreCase))
            list.Insert(0, _selectedModel);

        foreach (var m in list) ModelCombo.Items.Add(m);

        if (_selectedModel is not null)
            ModelCombo.SelectedItem = list.FirstOrDefault(
                x => x.Equals(_selectedModel, StringComparison.OrdinalIgnoreCase)) ?? _selectedModel;
        else if (ModelCombo.Items.Count > 0)
            ModelCombo.SelectedIndex = 0;

        _populatingModels = false;

        if (list.Count == 0)
            AddSystem("No Ollama models detected - is Ollama running on this machine?");

        _ = UpdateCapabilitiesAsync(_selectedModel ?? ModelCombo.SelectedItem as string);
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

        // Detected model details (family - size - context).
        var ctx = info.ContextLength >= 1024 ? $"{info.ContextLength / 1024}K ctx"
                : info.ContextLength > 0 ? $"{info.ContextLength} ctx" : "";
        var parts = new[] { info.Family, info.ParameterSize, ctx }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var label = string.Join(" - ", parts);
        if (!string.IsNullOrWhiteSpace(label))
            CapabilityPanel.Children.Add(MakeCapBadge("\U0001F9E9", label,
                $"Detected model: family {info.Family}, size {info.ParameterSize}, "
                + $"context window {info.ContextLength:N0} tokens."));

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
    private Border MakeCapBadge(string icon, string label, string tip, bool dim = false)
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
            Foreground = (Brush)FindResource(dim ? "TextDim" : "Text")
        });

        var badge = new Border
        {
            Background = (Brush)FindResource("PanelAlt"),
            BorderBrush = (Brush)FindResource("Border"),
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
            JsonNode spec = AgentSpecProvider.Load(_workingDir);
            _llmTemplate ??= JsonNode.Parse(spec["llm"]!.ToJsonString());
            if (_selectedModel is not null) ApplyModel(spec, _selectedModel);

            if (_settings.AutoTune)
                await AutoTuneSpecAsync(spec);

            // Respawn the server if the desired context size differs from what the
            // current backend was built with. OLLAMA_CONTEXT_LENGTH is applied at
            // process launch, so a reused (still-running) server must be torn down
            // for the new value to take effect. We're starting fresh here (_client
            // is null), so disposing any prior server is safe.
            if (_backendContext != _settings.ContextLength)
            {
                try { _backend.Dispose(); } catch { }
                _backend = BuildBackend();
            }

            bool healthy = await _backend.StartAsync(TimeSpan.FromSeconds(60));
            if (!healthy)
            {
                SetStatus("Server failed to start (see log)", AppDot.Down);
                AddError("The agent-server did not become healthy. Open the Server log for details.");
                SetSessionActive(false);
                return;
            }

            SetStatus("Creating conversation...", AppDot.Connecting);
            _client = new AgentServerClient(_backend.BaseUrl);
            _client.Update += OnAgentUpdate;
            _client.StatusChanged += OnServerStatus;
            _client.StatsUpdated += OnStats;
            _client.Compacted += OnCompacted;
            _client.Disconnected += OnDisconnected;

            await _client.StartConversationAsync(spec, _workingDir, _settings.MaxIterations);
            await _client.ConnectAsync();

            SetSessionActive(true);
            SetStatus("Ready", AppDot.Idle);
            InputBox.Focus();
            AddSystem($"Connected. Working in {_workingDir}");
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
            await _client.CondenseAsync();
        }
        catch (Exception ex)
        {
            AddError($"Compact failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remember the chosen model and refresh its capability strip. If a session is
    /// already live, hot-swap the conversation's LLM (and re-tune it) on the fly.
    /// </summary>
    private async void OnModelSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingModels) return;
        if (ModelCombo.SelectedItem is not string model) return;
        _selectedModel = model;

        _ = UpdateCapabilitiesAsync(model);

        // Before Start the choice is just remembered; once connected we hot-swap.
        if (_client is null || _llmTemplate is null) return;

        try
        {
            var llm = JsonNode.Parse(_llmTemplate.ToJsonString())!;
            llm["model"] = ToModelField(model);
            llm["native_tool_calling"] = false;
            if (_settings.AutoTune)
            {
                var rec = await RecommendAsync(model);
                ApplyTuningToLlm(llm, rec);
                _assumedContextWindow = rec.ContextLength;
            }
            await _client.SwitchLlmAsync(llm);
            AddSystem($"Switched model to {model}");
        }
        catch (Exception ex)
        {
            AddError($"Could not switch model: {ex.Message}");
        }
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
    private async Task<TuneResult> RecommendAsync(string model)
    {
        ModelInfo? info = _ollama is null ? null : await _ollama.GetModelInfoAsync(model);
        return ModelTuning.Recommend(model, info);
    }

    /// <summary>
    /// Apply newbie-friendly auto settings (temperature, reasoning, context) to the
    /// spec before a session starts, and adjust the app's context length to match.
    /// </summary>
    private async Task AutoTuneSpecAsync(JsonNode spec)
    {
        var model = _selectedModel ?? StripProvider(AgentSpecProvider.DescribeModel(spec));
        if (string.IsNullOrWhiteSpace(model)) return;

        var rec = await RecommendAsync(model);
        ApplyTuningToLlm(spec["llm"], rec);
        ApplyTuningToLlm(spec["condenser"]?["llm"], rec);

        _assumedContextWindow = rec.ContextLength;
        if (_settings.ContextLength != rec.ContextLength)
        {
            _settings.ContextLength = rec.ContextLength;
            _settings.Save();
        }

        AddSystem($"Auto-tuned {model}: temperature {rec.Temperature:0.0}, "
                  + $"context {rec.ContextLength:N0} tokens, reasoning {rec.ReasoningEffort}.");
    }

    private static void ApplyTuningToLlm(JsonNode? llm, TuneResult rec)
    {
        if (llm is not JsonObject o) return;
        o["temperature"] = rec.Temperature;
        o["reasoning_effort"] = rec.ReasoningEffort;
        o["max_input_tokens"] = rec.ContextLength;

        // Best-effort hint so native-ollama paths size their context correctly.
        if (o["litellm_extra_body"] is not JsonObject extra)
        {
            extra = new JsonObject();
            o["litellm_extra_body"] = extra;
        }
        extra["num_ctx"] = rec.ContextLength;
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
        SetRunning(true);

        try
        {
            await _client.SendUserMessageAsync(payload, images);
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

        var shown = ComposeShown(text, hasAttachments);
        InputBox.Clear();
        AddItem(ChatRole.User, "You (steer)", shown);
        try
        {
            var (note, images) = await ConsumeAttachmentsAsync();
            await _client.SendUserMessageAsync(text + note, images);
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

        var msg = _queue.Dequeue();
        UpdateQueueIndicator();
        msg.Bubble.Header = "You";   // it's no longer waiting
        _streamingItem = null;
        SetRunning(true);
        try
        {
            await _client.SendUserMessageAsync(msg.Text, msg.Images);
        }
        catch (Exception ex)
        {
            AddError($"Failed to send queued message: {ex.Message}");
            SetRunning(false);
        }
    }

    /// <summary>Show or hide the "N queued" indicator next to the input buttons.</summary>
    private void UpdateQueueIndicator()
    {
        int n = _queue.Count;
        QueueText.Text = n == 0 ? "" : n == 1 ? "1 queued" : $"{n} queued";
        QueueText.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAgentUpdate(AgentUpdate u)
        => Dispatcher.Invoke(() => ApplyUpdate(u));

    private void ApplyUpdate(AgentUpdate u)
    {
        if (u.Role == ChatRole.Agent && u.IsDelta)
        {
            _streamingItem ??= AddItem(ChatRole.Agent, u.Header, "");
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
                _streamingItem = null;
            }
            else
            {
                AddItem(ChatRole.Agent, u.Header, u.Text);
            }
            ScrollToBottom();
            return;
        }

        // Thought / Tool / Observation / Error all break the current stream.
        _streamingItem = null;
        AddItem(u.Role, u.Header, u.Text);
        ScrollToBottom();
    }

    private void OnServerStatus(string status)
        => Dispatcher.Invoke(() =>
        {
            var s = status.ToLowerInvariant();
            bool running = s.Contains("running");
            bool falling = _agentRunning && !running;   // a turn just finished
            SetRunning(running);
            if (!running)
            {
                SetStatus(Capitalize(status), AppDot.Idle);
                if (falling)
                {
                    _ = UpdateRepoBadgeAsync();   // the agent may have committed/branched
                    _ = RefreshTreeAsync();       // surface any files the agent changed
                    _ = FlushQueueAsync();        // send the next queued message, if any
                }
            }
            else
                SetStatus("Agent working...", AppDot.Working);
        });

    private void OnDisconnected(string reason)
        => Dispatcher.Invoke(() =>
        {
            AddSystem($"Disconnected: {reason}");
            SetRunning(false);
            SetStatus("Disconnected", AppDot.Down);
        });

    private long _assumedContextWindow = ModelTuning.ContextCap;

    private void OnStats(UsageStats s)
        => Dispatcher.Invoke(() =>
        {
            var window = s.ContextWindow > 0 ? s.ContextWindow : _assumedContextWindow;
            var used = s.PerTurnTokens;
            var pct = window > 0 ? Math.Clamp(100.0 * used / window, 0, 100) : 0;

            ContextBar.Value = pct;
            ContextBar.Foreground = pct >= 90 ? (Brush)FindResource("Err")
                : pct >= 75 ? (Brush)FindResource("Warn")
                : (Brush)FindResource("Accent");
            ContextText.Text = $"{Human(used)} / {Human(window)} ({pct:0}%)";

            TokensText.Text = $"Tokens: {Human(s.TotalTokens)}";
            CostText.Text = s.Cost > 0 ? $"${s.Cost:0.000}" : "";
        });

    private void OnCompacted()
        => Dispatcher.Invoke(() =>
        {
            AddSystem("Context auto-compacted (history summarized to free up space).");
            CompactText.Text = "Compacted";
            _ = ResetCompactLabelAsync();
        });

    private async Task ResetCompactLabelAsync()
    {
        await Task.Delay(2500);
        if (_client is not null) CompactText.Text = "Auto-compact: on";
    }

    // ---- chat helpers -------------------------------------------------------

    private ChatItem AddItem(ChatRole role, string header, string text)
    {
        var item = new ChatItem { Role = role, Header = header, Text = text };
        _chat.Add(item);
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
        });

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
    }

    /// <summary>Toggle whole-session UI (Start vs Stop, input availability, stats).</summary>
    private void SetSessionActive(bool active)
    {
        if (active)
        {
            StartButton.Content = "Stop";
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
            StartButton.Content = "Start";
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
    }

    private void ResetStatsDisplay()
    {
        ContextBar.Value = 0;
        ContextText.Text = "-- / --";
        TokensText.Text = "Tokens: 0";
        CostText.Text = "";
        CompactText.Text = "Auto-compact: on";
    }

    // ---- file explorer ------------------------------------------------------

    private List<FileNode> _treeRoots = new();
    private IReadOnlyDictionary<string, char> _statusMap = new Dictionary<string, char>();

    /// <summary>Rescan the working folder and git status into the explorer tree.</summary>
    private async Task RefreshTreeAsync()
    {
        if (string.IsNullOrWhiteSpace(_workingDir) || !Directory.Exists(_workingDir))
        {
            FileTree.ItemsSource = null;
            return;
        }

        _statusMap = await GitService.GetStatusMapAsync(_workingDir);
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
            CodeEditor.Text = System.Text.Encoding.UTF8.GetString(bytes);
            CodeEditor.SyntaxHighlighting =
                HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(path));
            _loadingEditor = false;

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
            CodeEditor.Text = File.ReadAllText(_editorPath);
            _loadingEditor = false;
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
        if (_client is not null)
        {
            _client.Update -= OnAgentUpdate;
            _client.StatusChanged -= OnServerStatus;
            _client.StatsUpdated -= OnStats;
            _client.Compacted -= OnCompacted;
            _client.Disconnected -= OnDisconnected;
            _client.Dispose();
            _client = null;
        }
        await Task.CompletedTask;
    }

    private void Cleanup()
    {
        try { _client?.Dispose(); } catch { }
        try { _backend.Dispose(); } catch { }
        try { _ollama?.Dispose(); } catch { }
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
