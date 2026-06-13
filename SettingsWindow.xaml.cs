using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// Modal settings editor. App-level preferences are written to AppSettings; the
/// model/LLM fields are read from and written back to ~/.openhands/agent_settings.json.
/// If that file doesn't exist yet, the model section is disabled but app settings
/// can still be edited.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private JsonNode? _spec;     // agent_settings.json (null if not configured yet)

    public SettingsWindow(AppSettings settings, IReadOnlyList<string> models)
    {
        InitializeComponent();
        _settings = settings;

        // App settings
        HostBox.Text = settings.Host;
        PortBox.Text = settings.Port.ToString();
        OllamaBox.Text = settings.OllamaUrl;
        ExePathBox.Text = settings.AgentServerPath;
        // 0 = auto; show it as an empty box.
        ContextLenBox.Text = settings.ContextLength > 0 ? settings.ContextLength.ToString() : "";
        MaxIterBox.Text = settings.MaxIterations.ToString();
        WorkDirBox.Text = settings.DefaultWorkingDir;
        AutoTuneBox.IsChecked = settings.AutoTune;

        // Model list
        foreach (var m in models) ModelBox.Items.Add(m);

        // Agent spec (model + llm)
        try
        {
            _spec = AgentSpecProvider.LoadRaw();
            var llm = _spec["llm"];
            var model = StripProvider(llm?["model"]?.GetValue<string>() ?? "");
            if (!string.IsNullOrEmpty(model) && !ModelBox.Items.Contains(model))
                ModelBox.Items.Insert(0, model);
            ModelBox.Text = model;

            ApiKeyBox.Text = llm?["api_key"]?.GetValue<string>() ?? "";
            BaseUrlBox.Text = llm?["base_url"]?.GetValue<string>() ?? "";
            TemperatureBox.Text = llm?["temperature"]?.GetValue<double>().ToString(CultureInfo.InvariantCulture) ?? "";
            SelectReasoning(llm?["reasoning_effort"]?.GetValue<string>());
            NonNativeBox.IsChecked = !(llm?["native_tool_calling"]?.GetValue<bool>() ?? false);
            CondenserBox.Text = (_spec["condenser"]?["max_size"]?.GetValue<int>() ?? 240).ToString();
        }
        catch
        {
            // No agent config yet - allow editing app settings only.
            ModelBox.IsEnabled = false;
            ApiKeyBox.IsEnabled = false;
            BaseUrlBox.IsEnabled = false;
            TemperatureBox.IsEnabled = false;
            ReasoningBox.IsEnabled = false;
            CondenserBox.IsEnabled = false;
            NonNativeBox.IsEnabled = false;
            ShowError("No saved model config found (~/.openhands/agent_settings.json). "
                      + "Run the OpenHands CLI once to configure a model; app settings below still apply.");
        }

        // Refresh GitHub status when the window opens and whenever it regains focus
        // (so it updates after the user completes sign-in in the terminal).
        Activated += async (_, _) => await RefreshGitHubStatusAsync();
    }

    // ---- GitHub account -----------------------------------------------------

    private bool _ghRefreshing;

    /// <summary>Reflect the current gh auth state in the GitHub section.</summary>
    private async Task RefreshGitHubStatusAsync()
    {
        if (_ghRefreshing) return;
        _ghRefreshing = true;
        try
        {
            if (!GitService.GhAvailable)
            {
                GitHubStatusText.Text = "GitHub CLI (gh) is not installed.";
                GhSignInButton.IsEnabled = false;
                GhSignOutButton.IsEnabled = false;
                return;
            }

            var account = await GitService.GhAccountAsync();
            if (account is null)
            {
                GitHubStatusText.Text = "Not signed in.";
                GhSignInButton.IsEnabled = true;
                GhSignOutButton.IsEnabled = false;
            }
            else
            {
                GitHubStatusText.Text = $"Signed in as {account}.";
                GhSignInButton.IsEnabled = true;   // allow signing in as a different account
                GhSignOutButton.IsEnabled = true;
            }
        }
        finally
        {
            _ghRefreshing = false;
        }
    }

    /// <summary>Launch gh's interactive web sign-in in a visible terminal window.</summary>
    private void OnGhSignIn(object sender, RoutedEventArgs e)
    {
        try
        {
            // gh's web login prints a one-time code and opens the browser, so it
            // needs a real terminal the user can interact with.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoExit -Command \"gh auth login --web --hostname github.com\"",
                UseShellExecute = true
            });
            GitHubStatusText.Text = "Finish sign-in in the terminal window, then return here.";
        }
        catch (Exception ex)
        {
            ShowError($"Could not start GitHub sign-in: {ex.Message}");
        }
    }

    /// <summary>Sign out of GitHub via gh, after confirming.</summary>
    private async void OnGhSignOut(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "Sign out of GitHub (github.com)?\n\n"
            + "The agent won't be able to use GitHub features (PRs, issues, private "
            + "clones) until you sign in again.",
            "Sign out of GitHub?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        GhSignOutButton.IsEnabled = false;
        var (ok, message) = await GitService.GhLogoutAsync();
        if (!ok && !string.IsNullOrWhiteSpace(message))
            MessageBox.Show(this, message, "Sign out", MessageBoxButton.OK, MessageBoxImage.Warning);

        await RefreshGitHubStatusAsync();
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

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select agent-server.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true) ExePathBox.Text = dlg.FileName;
    }

    private void OnBrowseDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select default working folder" };
        if (dlg.ShowDialog(this) == true) WorkDirBox.Text = dlg.FolderName;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        if (!int.TryParse(PortBox.Text, out var port) || port is <= 0 or > 65535)
        { ShowError("Port must be a number between 1 and 65535."); return; }
        // Empty = auto (0). Otherwise it must be a real number >= 1024.
        int ctx;
        var ctxText = ContextLenBox.Text.Trim();
        if (ctxText.Length == 0)
            ctx = 0;
        else if (!int.TryParse(ctxText, out ctx) || ctx < 1024)
        { ShowError("Max context must be a number >= 1024, or blank for auto."); return; }
        if (!int.TryParse(MaxIterBox.Text, out var maxIter) || maxIter < 1)
        { ShowError("Max iterations must be a positive number."); return; }

        double? temperature = null;
        if (!string.IsNullOrWhiteSpace(TemperatureBox.Text))
        {
            if (!double.TryParse(TemperatureBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            { ShowError("Temperature must be a number (or blank)."); return; }
            temperature = t;
        }

        int condenser = 240;
        if (_spec is not null && (!int.TryParse(CondenserBox.Text, out condenser) || condenser < 10))
        { ShowError("Auto-compact threshold must be a number >= 10."); return; }

        // Save app settings
        _settings.Host = HostBox.Text.Trim();
        _settings.Port = port;
        _settings.OllamaUrl = OllamaBox.Text.Trim();
        _settings.AgentServerPath = ExePathBox.Text.Trim();
        _settings.ContextLength = ctx;
        _settings.MaxIterations = maxIter;
        _settings.DefaultWorkingDir = WorkDirBox.Text.Trim();
        _settings.AutoTune = AutoTuneBox.IsChecked == true;
        _settings.Save();

        // Save model/agent config
        if (_spec is not null)
        {
            try
            {
                var model = ToModelField(ModelBox.Text.Trim());
                var apiKey = ApiKeyBox.Text.Trim();
                var baseUrl = BaseUrlBox.Text.Trim();
                var reasoning = (ReasoningBox.SelectedItem as ComboBoxItem)?.Content as string ?? "high";
                var nonNative = NonNativeBox.IsChecked == true;

                ApplyLlm(_spec["llm"], model, apiKey, baseUrl, temperature, reasoning, nonNative);
                ApplyLlm(_spec["condenser"]?["llm"], model, apiKey, baseUrl, temperature, reasoning, nonNative);
                if (_spec["condenser"] is JsonObject cond) cond["max_size"] = condenser;

                AgentSpecProvider.Save(_spec);
            }
            catch (Exception ex)
            {
                ShowError($"Could not save model settings: {ex.Message}");
                return;
            }
        }

        DialogResult = true;
        Close();
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

    private static string StripProvider(string model)
        => model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ? model["openai/".Length..] : model;

    private static string ToModelField(string name)
        => string.IsNullOrEmpty(name) ? name : (name.Contains('/') ? name : "openai/" + name);

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
