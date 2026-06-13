using System.Windows;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// Lightweight help window for VRAM-tiered model picks. Kept separate from the main
/// screen so guidance is available without cluttering the normal workflow.
/// </summary>
public partial class ModelRecommendationsWindow : Window
{
    public ModelRecommendationsWindow()
    {
        InitializeComponent();
        RecommendationsBox.Text = ModelAdvisor.RecommendationsWithCommands;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
