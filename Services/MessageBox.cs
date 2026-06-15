using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace StayVibin.Services;

public enum MessageBoxButton { OK, YesNo }
public enum MessageBoxResult { OK, Yes, No, Cancel }
public enum MessageBoxImage { Information, Question, Warning, Error }

/// <summary>
/// Simple custom MessageBox class mimicking WPF's MessageBox but built on Avalonia UI.
/// Supports both synchronous-looking task blocking and traditional dialog results.
/// </summary>
public static class MessageBox
{
    public static void Show(string message, string title = "StayVibin", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        _ = ShowAsync(null, message, title, button);
    }

    public static void Show(Window? owner, string message, string title = "StayVibin", MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        _ = ShowAsync(owner, message, title, button);
    }

    public static async Task<MessageBoxResult> ShowAsync(Window? owner, string message, string title = "StayVibin", MessageBoxButton button = MessageBoxButton.OK)
    {
        var result = MessageBoxResult.Cancel;

        var tcs = new TaskCompletionSource<MessageBoxResult>();

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var grid = new Grid
            {
                Margin = new Avalonia.Thickness(20),
                RowDefinitions = new RowDefinitions("*,Auto"),
                RowSpacing = 16
            };

            var body = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                FontSize = 13.5,
                Foreground = Brushes.White
            };

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                MaxHeight = 400,
                Content = body
            };
            grid.Children.Add(scroll);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10
            };
            Grid.SetRow(buttonPanel, 1);
            grid.Children.Add(buttonPanel);

            var w = new Window
            {
                Title = title,
                Content = grid,
                MinWidth = 340,
                MaxWidth = 580,
                MinHeight = 130,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = SolidColorBrush.Parse("#121212"),
                SystemDecorations = SystemDecorations.Full
            };

            if (owner is not null)
            {
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            if (button == MessageBoxButton.YesNo)
            {
                var yes = new Button { Content = "Yes", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
                var no = new Button { Content = "No", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "flat" } };

                yes.Click += (_, _) => { result = MessageBoxResult.Yes; w.Close(); };
                no.Click += (_, _) => { result = MessageBoxResult.No; w.Close(); };

                buttonPanel.Children.Add(yes);
                buttonPanel.Children.Add(no);
            }
            else
            {
                var ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" } };
                ok.Click += (_, _) => { result = MessageBoxResult.OK; w.Close(); };
                buttonPanel.Children.Add(ok);
            }

            w.Closed += (_, _) => tcs.TrySetResult(result);

            try
            {
                if (owner is not null)
                    await w.ShowDialog(owner);
                else
                    w.Show();
            }
            catch
            {
                w.Show();
            }
        });

        return await tcs.Task;
    }
}
