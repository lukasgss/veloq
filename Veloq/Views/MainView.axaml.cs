using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Veloq.Data;
using Veloq.ViewModels;

namespace Veloq.Views;

public sealed partial class MainView : UserControl
{
    private readonly TextEditor? _editor;
    private MainViewModel? _vm;
    private CompletionWindow? _completionWindow;
    private int _completionRequest;
    private bool _syncing;

    public MainView()
    {
        InitializeComponent();

        _editor = Editor;
        _editor.SyntaxHighlighting = LoadCSharpDark();
        _editor.Options.IndentationSize = 4;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.TextChanged += OnEditorTextChanged;
        _editor.TextArea.TextEntered += OnEditorTextEntered;
        _editor.TextArea.KeyDown += OnEditorKeyDown;

        DataContextChanged += OnDataContextChanged;
    }

    private static IHighlightingDefinition LoadCSharpDark()
    {
        using Stream stream = AssetLoader.Open(new Uri("avares://Veloq/Assets/CSharpDark.xshd"));
        using XmlReader reader = XmlReader.Create(stream);

        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm?.PropertyChanged -= OnVmPropertyChanged;

        _vm = DataContext as MainViewModel;
        if (_vm is null || _editor is null)
        {
            return;
        }

        _vm.PropertyChanged += OnVmPropertyChanged;
        SetEditorText(_vm.QueryText);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.QueryText) && _vm is not null)
        {
            SetEditorText(_vm.QueryText);
        }
    }

    private void SetEditorText(string text)
    {
        if (_editor is null || _syncing || _editor.Text == text)
        {
            return;
        }

        _syncing = true;
        _editor.Text = text;
        _syncing = false;
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncing || _vm is null || _editor is null)
        {
            return;
        }

        _syncing = true;
        _vm.QueryText = _editor.Text;
        _syncing = false;
    }

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".")
        {
            ObserveFault(ShowCompletionsAsync(explicitRequest: false));
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            ObserveFault(ShowCompletionsAsync(explicitRequest: true));
        }
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            failedTask => Debug.WriteLine(failedTask.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ShowCompletionsAsync(bool explicitRequest)
    {
        if (_editor is null || _vm is null)
        {
            return;
        }

        string requestedText = _editor.Text;
        int requestedPosition = _editor.CaretOffset;
        int request = ++_completionRequest;
        IReadOnlyList<CompletionSuggestion> suggestions;

        try
        {
            suggestions = await _vm.GetCompletionsAsync(requestedText, requestedPosition);
        }
        catch
        {
            return;
        }

        if (request != _completionRequest ||
            _editor.CaretOffset < requestedPosition ||
            _editor.Text.Length < requestedPosition ||
            !_editor.Text.AsSpan(0, requestedPosition)
                .SequenceEqual(requestedText.AsSpan(0, requestedPosition)) ||
            suggestions.Count == 0)
        {
            return;
        }

        _completionWindow?.Close();
        CompletionWindow window = new(_editor.TextArea);
        window.CompletionList.MinWidth = 460;
        window.CompletionList.MaxHeight = 340;
        _completionWindow = window;

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completionWindow, window))
            {
                _completionWindow = null;
            }
        };

        if (explicitRequest)
        {
            window.StartOffset = FindWordStart(_editor.Text, requestedPosition);
        }
        else
        {
            window.StartOffset = requestedPosition;
        }

        foreach (CompletionSuggestion suggestion in suggestions)
        {
            window.CompletionList.CompletionData.Add(new EditorCompletionData(suggestion));
        }

        window.Show();
    }

    private static int FindWordStart(string text, int position)
    {
        while (position > 0 && (char.IsLetterOrDigit(text[position - 1]) || text[position - 1] == '_'))
        {
            position--;
        }

        return position;
    }

    private sealed class EditorCompletionData(CompletionSuggestion suggestion) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => suggestion.Text;
        public object Content { get; } = CreateContent(suggestion);
        public object Description => suggestion.Description;
        public double Priority => suggestion.Priority;

        public void Complete(
            TextArea textArea,
            ISegment completionSegment,
            EventArgs insertionRequestEventArgs)
        {
            int insertionOffset = completionSegment.Offset;
            bool isMethod = suggestion.Kind is "Method" or "Extension";
            bool hasParenthesis = completionSegment.EndOffset < textArea.Document.TextLength &&
                                  textArea.Document.GetCharAt(completionSegment.EndOffset) == '(';
            bool hasEmptyParentheses = hasParenthesis &&
                                       completionSegment.EndOffset + 1 < textArea.Document.TextLength &&
                                       textArea.Document.GetCharAt(completionSegment.EndOffset + 1) == ')';

            if (!isMethod)
            {
                textArea.Document.Replace(completionSegment, Text);
                return;
            }

            string replacement = hasParenthesis ? Text : Text + "()";
            textArea.Document.Replace(completionSegment, replacement);

            bool placeAfterCall = suggestion.CanInvokeWithoutArguments && (!hasParenthesis || hasEmptyParentheses);

            textArea.Caret.Offset = insertionOffset + Text.Length + (placeAfterCall ? 2 : 1);
        }

        private static Grid CreateContent(CompletionSuggestion item)
        {
            (string glyph, string color) = item.Kind switch
            {
                "Table" => ("▦", "#4EC9B0"),
                "Column" => ("◆", "#9CDCFE"),
                "Navigation" => ("↗", "#4EC9B0"),
                "Property" => ("◇", "#9CDCFE"),
                "Field" => ("■", "#75BEFF"),
                "Variable" => ("𝑥", "#DCDCAA"),
                "Extension" => ("ƒ", "#C586C0"),
                "Method" => ("ƒ", "#DCDCAA"),
                "Event" => ("⚡", "#F48771"),
                "Type" => ("◫", "#4EC9B0"),
                "Namespace" => ("▱", "#B4B4B4"),
                _ => ("·", "#B4B4B4"),
            };

            TextBlock icon = new()
            {
                Text = glyph,
                Width = 18,
                Foreground = new SolidColorBrush(Color.Parse(color)),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            TextBlock name = new()
            {
                Text = item.Text,
                Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };

            TextBlock detail = new()
            {
                Text = item.Detail,
                MaxWidth = 190,
                Foreground = new SolidColorBrush(Color.Parse("#858585")),
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid row = new()
            {
                Width = 430,
                MinHeight = 23,
                Margin = new Thickness(3, 0),
                ColumnDefinitions = new ColumnDefinitions("18,7,*,12,Auto"),
            };

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(name, 2);
            Grid.SetColumn(detail, 4);
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(detail);

            return row;
        }
    }
}
