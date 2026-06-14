using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// One-click model manager ("app store"): lists installed Ollama models with their
/// on-disk size, lets the operator install (download) curated or arbitrary models
/// with a live progress bar, and remove models with a button - all without touching
/// PowerShell. Backed entirely by the Ollama HTTP API via <see cref="OllamaClient"/>.
/// </summary>
public partial class ModelStoreWindow : Window
{
    // Shared client owned by MainWindow - do NOT dispose it here.
    private readonly OllamaClient _ollama;

    // Cancels an in-flight pull when the window closes.
    private readonly CancellationTokenSource _cts = new();

    // Per-download cancel source (linked to _cts) so the Stop button can cancel just
    // the current pull without tearing down the window.
    private CancellationTokenSource? _pullCts;

    private bool _busy;

    /// <summary>
    /// True if the installed set changed while the store was open, so the caller can
    /// refresh the main model dropdown when the dialog closes.
    /// </summary>
    public bool ModelsChanged { get; private set; }

    public ObservableCollection<InstalledRow> Installed { get; } = new();
    public ObservableCollection<CatalogRow> Available { get; } = new();

    // Grouped/sorted/filtered view over Available, driven by the filter controls.
    private ListCollectionView _catalogView = null!;

    // Optional model to begin downloading as soon as the store opens (set when the
    // user clicks an Install suggestion in chat).
    private readonly string? _autoInstall;

    public ModelStoreWindow(OllamaClient ollama, string? autoInstall = null)
    {
        _ollama = ollama ?? throw new ArgumentNullException(nameof(ollama));
        _autoInstall = string.IsNullOrWhiteSpace(autoInstall) ? null : autoInstall.Trim();
        InitializeComponent();

        InstalledList.ItemsSource = Installed;

        // Group the catalog by VRAM tier and sort so tiers go small -> large and
        // Recommended picks float to the top of each tier. Grouping follows the sort
        // order, so the group headers also appear smallest-tier first. The view
        // tracks the ObservableCollection, so rebuilding Available on refresh
        // re-sorts/re-groups automatically.
        _catalogView = new ListCollectionView(Available)
        {
            // Live filter set from the search box / category / recommended toggle.
            Filter = CatalogFilter,
        };
        _catalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogRow.TierOrder), ListSortDirection.Ascending));
        _catalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogRow.Recommended), ListSortDirection.Descending));
        _catalogView.SortDescriptions.Add(new SortDescription(nameof(CatalogRow.Name), ListSortDirection.Ascending));
        _catalogView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogRow.Tier)));
        CatalogList.ItemsSource = _catalogView;

        Loaded += OnLoadedAsync;
        Closing += (_, _) => { try { _cts.Cancel(); } catch { } finally { _cts.Dispose(); } };
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        // If launched to install a specific model, and it is not already present,
        // start the download immediately so the user does not have to click again.
        if (_autoInstall is not null
            && !Installed.Any(r => string.Equals(r.Name, _autoInstall, StringComparison.OrdinalIgnoreCase)))
        {
            await InstallAsync(_autoInstall);
        }
    }

    /// <summary>Reload the installed list, disk summary, and catalog install-state.</summary>
    private async Task RefreshAsync()
    {
        var installed = await _ollama.ListInstalledAsync(_cts.Token);

        Installed.Clear();
        long totalBytes = 0;
        var installedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in installed)
        {
            totalBytes += m.SizeBytes;
            installedNames.Add(m.Name);

            // Estimate the accuracy/Recommended badges for this installed tag. We pull
            // /api/show (cached) so models whose tag lacks a size (e.g. "...:latest")
            // can still be rated from their real parameter count.
            ModelInfo? info = await _ollama.GetModelInfoAsync(m.Name, _cts.Token);
            var (accuracy, recommended) = ModelAdvisor.AssessInstalled(m.Name, info);
            Installed.Add(new InstalledRow(m.Name, FormatBytes(m.SizeBytes), accuracy, recommended));
        }
        NoneInstalled.Visibility = Installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Catalog: mark which curated models are already installed (tag match, with
        // an implicit ":latest" fallback so "qwen3:14b" matches an installed
        // "qwen3:14b" exactly and "gemma4" would match "gemma4:latest").
        Available.Clear();
        foreach (var e in ModelAdvisor.Catalog)
        {
            bool present = installedNames.Contains(e.Model)
                           || installedNames.Contains(e.Model + ":latest");
            Available.Add(new CatalogRow(e.Model, e.Tier, e.TierOrder, e.Category,
                                         e.Recommended, e.Accuracy, e.Note, present));
        }
        _catalogView?.Refresh();

        DiskSummary.Text = BuildDiskSummary(totalBytes);
    }

    // --- Catalog filtering --------------------------------------------------

    /// <summary>
    /// Predicate behind the catalog's live filter: matches the search text (against
    /// the tag and note), the selected category, and the "recommended only" toggle.
    /// Tolerant of being called before the controls are created (returns true).
    /// </summary>
    private bool CatalogFilter(object obj)
    {
        if (obj is not CatalogRow row) return false;

        if (RecommendedOnly?.IsChecked == true && !row.Recommended) return false;

        var category = (CategoryFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
        if (!string.IsNullOrEmpty(category) && category != "All categories"
            && !string.Equals(category, row.Category, StringComparison.OrdinalIgnoreCase))
            return false;

        // Minimum-accuracy filter (Any / Fair+ / Good+ / Top local only).
        var acc = (AccuracyFilter?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
        var minAccuracy = acc switch
        {
            "Fair or better"  => ModelAdvisor.AccuracyTier.Fair,
            "Good or better"  => ModelAdvisor.AccuracyTier.Good,
            "Top local only"  => ModelAdvisor.AccuracyTier.VeryGood,
            _                 => (ModelAdvisor.AccuracyTier?)null,
        };
        if (minAccuracy is { } min && row.Accuracy < min) return false;

        var q = SearchBox?.Text?.Trim();
        if (!string.IsNullOrEmpty(q)
            && row.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
            && row.Note.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        return true;
    }

    /// <summary>Re-run the catalog filter when any filter control changes.</summary>
    private void OnFilterChanged(object sender, RoutedEventArgs e) => _catalogView?.Refresh();

    /// <summary>Compose "Models use X - Y free on Z" using the Ollama models directory.</summary>
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

    /// <summary>Where Ollama keeps model blobs (OLLAMA_MODELS override, else ~/.ollama/models).</summary>
    private static string ModelsDirectory()
    {
        var env = Environment.GetEnvironmentVariable("OLLAMA_MODELS");
        if (!string.IsNullOrWhiteSpace(env)) return env!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".ollama", "models");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double gb = bytes / 1024d / 1024d / 1024d;
        if (gb >= 1) return $"{gb:0.0} GB";
        double mb = bytes / 1024d / 1024d;
        return $"{mb:0} MB";
    }

    // --- Install -----------------------------------------------------------

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string model)
            await InstallAsync(model);
    }

    private async void OnInstallByName(object sender, RoutedEventArgs e)
        => await InstallAsync(InstallNameBox.Text);

    private async void OnInstallNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await InstallAsync(InstallNameBox.Text);
    }

    private async Task InstallAsync(string model)
    {
        model = (model ?? "").Trim();
        if (_busy || string.IsNullOrWhiteSpace(model)) return;

        SetBusy(true);
        // A download can take a while, so allow cancelling just this pull via Stop.
        _pullCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        StopInstallButton.Visibility = Visibility.Visible;
        StopInstallButton.IsEnabled = true;
        ProgressText.Text = $"Preparing to install {model}...";
        ProgressBarCtl.Value = 0;
        try
        {
            // Ollama streams a progress line per chunk (potentially many per second for
            // a multi-GB pull). Throttle UI updates to whole-percent or status changes
            // so we don't flood the dispatcher / churn the GC. Progress<T> created on
            // the UI thread marshals callbacks back to it.
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

            bool ok = await _ollama.PullModelAsync(model, progress, _pullCts.Token);
            if (ok)
            {
                ModelsChanged = true;
                _ollama.ClearCache();   // re-probe capabilities for the new model later
                InstallNameBox.Clear();
                await RefreshAsync();
            }
            else
            {
                MessageBox.Show(this,
                    $"Install of '{model}' did not complete. Check the model name and try again.",
                    "Model Store", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closing or user pressed Stop - nothing to report. The partial
            // download is not registered as a model, so it simply won't appear.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not install '{model}'.\n\n{ex.Message}",
                "Model Store", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopInstallButton.Visibility = Visibility.Collapsed;
            _pullCts?.Dispose();
            _pullCts = null;
            SetBusy(false);
        }
    }

    /// <summary>Stop button: cancel just the current download.</summary>
    private void OnStopInstall(object sender, RoutedEventArgs e)
    {
        if (_pullCts is null) return;
        ProgressText.Text = "Stopping...";
        StopInstallButton.IsEnabled = false;
        _pullCts.Cancel();
    }

    // --- Remove ------------------------------------------------------------

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not FrameworkElement fe || fe.Tag is not string model) return;

        var confirm = MessageBox.Show(this,
            $"Remove '{model}' from disk? You can reinstall it later.",
            "Remove model", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        ProgressText.Text = $"Removing {model}...";
        ProgressBarCtl.IsIndeterminate = true;
        try
        {
            bool ok = await _ollama.DeleteModelAsync(model, _cts.Token);
            if (ok)
            {
                ModelsChanged = true;
                _ollama.ClearCache();
                await RefreshAsync();
            }
            else
            {
                MessageBox.Show(this, $"Could not remove '{model}'.",
                    "Model Store", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not remove '{model}'.\n\n{ex.Message}",
                "Model Store", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ProgressBarCtl.IsIndeterminate = false;
            SetBusy(false);
        }
    }

    // --- UI plumbing -------------------------------------------------------

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try { _ollama.ClearCache(); await RefreshAsync(); }
        finally { SetBusy(false); }
    }

    private void OnOpenGuide(object sender, RoutedEventArgs e)
        => new ModelRecommendationsWindow { Owner = this }.ShowDialog();

    /// <summary>Toggle controls and the progress panel for the duration of an operation.</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) ProgressBarCtl.IsIndeterminate = false;

        InstallNameBox.IsEnabled = !busy;
        InstallByNameButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        InstalledList.IsEnabled = !busy;
        CatalogList.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        CategoryFilter.IsEnabled = !busy;
        AccuracyFilter.IsEnabled = !busy;
        RecommendedOnly.IsEnabled = !busy;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// An installed model row: tag, human-readable size, and the same accuracy /
/// Recommended badges the catalog uses (estimated from the tag/parameters).
/// </summary>
public sealed class InstalledRow
{
    public string Name { get; }
    public string SizeText { get; }
    public ModelAdvisor.AccuracyTier Accuracy { get; }
    public bool Recommended { get; }

    public InstalledRow(string name, string sizeText,
                        ModelAdvisor.AccuracyTier accuracy, bool recommended)
    {
        Name = name;
        SizeText = sizeText;
        Accuracy = accuracy;
        Recommended = recommended;
    }

    public string AccuracyText => ModelAdvisor.AccuracyLabel(Accuracy);
    public Brush AccuracyBrush => BadgeBrush.For(Accuracy);
    public Visibility RecommendedVisibility => Recommended ? Visibility.Visible : Visibility.Collapsed;
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
    public bool IsInstalled { get; }

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

    public Visibility InstallVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecommendedVisibility => Recommended ? Visibility.Visible : Visibility.Collapsed;

    // Accuracy badge: honest, color-coded label so users from paid tools (Cursor,
    // Claude, Codex) know what to expect before installing.
    public string AccuracyText => ModelAdvisor.AccuracyLabel(Accuracy);

    public Brush AccuracyBrush => BadgeBrush.For(Accuracy);
}

/// <summary>Shared lookup so installed and catalog rows color accuracy badges alike.</summary>
internal static class BadgeBrush
{
    public static Brush For(ModelAdvisor.AccuracyTier a)
        => (Brush)Application.Current.Resources[ModelAdvisor.AccuracyBrushKey(a)];
}
