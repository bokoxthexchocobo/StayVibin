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
    private bool _isThinking;

    public ChatRole Role { get; init; }

    public bool IsUser => Role == ChatRole.User;
    public bool IsThought => Role == ChatRole.Thought;

    /// <summary>
    /// True while this reasoning block is the agent's most recent activity, so the
    /// view shows an animated "Assistant is thinking..." header. Cleared once the
    /// agent produces its next output (answer/tool) or the turn ends, at which point
    /// the header settles to a static "Thought" label.
    /// </summary>
    public bool IsThinking
    {
        get => _isThinking;
        set { _isThinking = value; OnPropertyChanged(); }
    }
    public bool IsTool => Role == ChatRole.Tool;
    public bool IsObservation => Role == ChatRole.Observation;
    public bool IsError => Role == ChatRole.Error;
    public bool IsSystem => Role == ChatRole.System;
    public bool IsAgent => Role == ChatRole.Agent;

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
