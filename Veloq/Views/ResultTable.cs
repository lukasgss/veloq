using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Veloq.Data;

namespace Veloq.Views;

public sealed class ResultTable : Grid
{
    public static readonly StyledProperty<QueryResult?> ResultProperty =
        AvaloniaProperty.Register<ResultTable, QueryResult?>(nameof(Result));

    private static readonly FontFamily ResultFont = new("Cascadia Code,Consolas,monospace");
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E5E7EB"));
    private static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));

    static ResultTable()
    {
        ResultProperty.Changed.AddClassHandler<ResultTable>((table, _) => table.Rebuild());
    }

    public QueryResult? Result
    {
        get => GetValue(ResultProperty);
        set => SetValue(ResultProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        if (Result is not { } result)
        {
            return;
        }

        if (result.RowCount == 0)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Children.Add(CreateCell("(no rows)", 0, 0, isLastColumn: true));
            return;
        }

        int columnCount = result.Columns.Count + 1;
        for (int column = 0; column < columnCount; column++)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        AddRowDefinition();
        AddRowDefinition(height: 7);
        AddCell("#", row: 0, column: 0, isHeader: true, columnCount);
        for (int column = 0; column < result.Columns.Count; column++)
        {
            AddCell(result.Columns[column], row: 0, column: column + 1, isHeader: true, columnCount);
        }

        for (int column = 0; column < columnCount; column++)
        {
            Border rule = new()
            {
                Height = 1,
                Margin = new Thickness(0, 2, column == columnCount - 1 ? 0 : 16, 4),
                Background = RuleBrush,
                VerticalAlignment = VerticalAlignment.Top,
            };
            SetRow(rule, 1);
            SetColumn(rule, column);
            Children.Add(rule);
        }

        for (int row = 0; row < result.Rows.Count; row++)
        {
            int gridRow = row + 2;
            AddRowDefinition();
            AddCell((row + 1).ToString(), gridRow, 0, isHeader: false, columnCount);

            string[] values = result.Rows[row];
            for (int column = 0; column < result.Columns.Count; column++)
            {
                string text = column < values.Length ? values[column] : string.Empty;

                AddCell(text, gridRow, column + 1, isHeader: false, columnCount);
            }
        }

        if (result.RowCount > result.Rows.Count)
        {
            int gridRow = result.Rows.Count + 2;

            AddRowDefinition();

            SelectableTextBlock moreRows = CreateCell(
                $"... {result.RowCount - result.Rows.Count} more rows",
                gridRow,
                0,
                isLastColumn: true);

            SetColumnSpan(moreRows, columnCount);
            Children.Add(moreRows);
        }
    }

    private void AddCell(
        string text,
        int row,
        int column,
        bool isHeader,
        int columnCount)
    {
        SelectableTextBlock cell = CreateCell(text, row, column, column == columnCount - 1);
        if (isHeader)
        {
            cell.FontWeight = FontWeight.SemiBold;
        }

        Children.Add(cell);
    }

    private static SelectableTextBlock CreateCell(string text, int row, int column, bool isLastColumn)
    {
        SelectableTextBlock cell = new()
        {
            Text = text,
            FontFamily = ResultFont,
            FontSize = 13,
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, isLastColumn ? 0 : 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        SetRow(cell, row);
        SetColumn(cell, column);
        return cell;
    }

    private void AddRowDefinition(double? height = null)
    {
        RowDefinitions.Add(new RowDefinition(height is null ? GridLength.Auto : new GridLength(height.Value)));
    }
}
