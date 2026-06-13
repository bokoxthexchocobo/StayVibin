using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace StayVibin.Models;

/// <summary>
/// Who/what produced a chat line. Drives the bubble styling in MainWindow.xaml
/// (alignment, color, monospace for tool/observation, etc.).
/// </summary>
public enum ChatRole
{
    User,         // a message the human typed (or steered)
    Agent,        // the assistant's reply
    Thought,      // the agent's internal reasoning
    Tool,         // a tool/action the agent invoked
    Observation,  // the result returned by a tool
    System,       // app-generated status/info notices
    Error         // an error surfaced to the user
}

/// <summary>
/// One rendered line in the conversation. Implements INotifyPropertyChanged so a
/// streaming agent message can grow in place as tokens arrive.
/// </summary>
public sealed class ChatItem : INotifyPropertyChanged
{
    private string _text = string.Empty;
    private string _header = string.Empty;

    public ChatRole Role { get; init; }

    public string Header
    {
        get => _header;
        set { _header = value; OnPropertyChanged(); }
    }

    public string Text
    {
        get => _text;
        set { _text = value; OnPropertyChanged(); }
    }

    public void Append(string fragment)
    {
        _text += fragment;
        OnPropertyChanged(nameof(Text));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
