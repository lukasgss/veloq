using System;
using System.ComponentModel;
using System.IO;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Veloq.ViewModels;

namespace Veloq.Views;

public sealed partial class MainView : UserControl
{
    private readonly TextEditor? _editor;
    private MainViewModel? _vm;
    private bool _syncing;

    public MainView()
    {
        InitializeComponent();

        _editor = Editor;
        _editor.SyntaxHighlighting = LoadCSharpDark();
        _editor.Options.IndentationSize = 4;
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.TextChanged += OnEditorTextChanged;

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
}
