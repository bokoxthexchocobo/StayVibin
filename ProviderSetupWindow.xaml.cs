using System.Windows;
using System.Windows.Controls;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// First-run onboarding dialog. Collects the local AI provider, its URL, and the
/// model to use, then the caller writes a default agent_settings.json from the
/// result. Only Ollama is offered today; the provider list is here so more local
/// providers can be added without reworking the flow.
/// </summary>
public partial class ProviderSetupWindow : Window
{
    /// <summary>Chosen provider label (e.g. "Ollama").</summary>
    public string Provider { get; private set; } = "Ollama";

    /// <summary>Chosen provider base URL (e.g. http://localhost:11434).</summary>
    public string OllamaUrl { get; private set; } = "http://localhost:11434";

    /// <summary>Chosen model tag (e.g. "qwen2.5-coder:14b").</summary>
    public string Model { get; private set; } = "";

    public ProviderSetupWindow(AppSettings settings)
    {
        InitializeComponent();

        ProviderBox.Items.Add("Ollama (local)");
        ProviderBox.SelectedIndex = 0;

        UrlBox.Text = string.IsNullOrWhiteSpace(settings.OllamaUrl)
            ? "http://localhost:11434"
            : settings.OllamaUrl;

        Loaded += async (_, _) => await LoadModelsAsync();
    }

    /// <summary>Query Ollama for installed chat models and fill the dropdown.</summary>
    private async Task LoadModelsAsync()
    {
        HideError();
        DetectedBox.Items.Clear();
        DetectedBox.Items.Add("Detecting models...");
        DetectedBox.SelectedIndex = 0;
        RefreshButton.IsEnabled = false;

        try
        {
            using var ollama = new OllamaClient(UrlBox.Text.Trim());
            var models = await ollama.ListModelsAsync();

            DetectedBox.Items.Clear();
            if (models.Count == 0)
            {
                DetectedBox.Items.Add("No models found - is Ollama running?");
                DetectedBox.SelectedIndex = 0;
                return;
            }

            foreach (var m in models) DetectedBox.Items.Add(m);

            // Default the model field to the first detected model if still blank.
            if (string.IsNullOrWhiteSpace(ModelBox.Text))
            {
                ModelBox.Text = models[0];
                DetectedBox.SelectedIndex = 0;
            }
        }
        catch
        {
            DetectedBox.Items.Clear();
            DetectedBox.Items.Add("Could not reach Ollama");
            DetectedBox.SelectedIndex = 0;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadModelsAsync();

    private void OnDetectedSelected(object sender, SelectionChangedEventArgs e)
    {
        // Copy a real model name into the editable field; ignore status placeholders.
        if (DetectedBox.SelectedItem is string s
            && !s.Contains("...") && !s.StartsWith("No models") && !s.StartsWith("Could not"))
        {
            ModelBox.Text = s;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var model = ModelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowError("Enter a model name (or pick one from the detected list).");
            return;
        }
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("Enter the Ollama URL (e.g. http://localhost:11434).");
            return;
        }

        Provider = "Ollama";
        OllamaUrl = url;
        Model = model;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
