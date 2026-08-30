using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Veloq.Data;
using Veloq.Views;
using Xunit;

namespace Veloq.Tests;

public sealed class ResultTableTests
{
    static ResultTableTests()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();
    }

    [Theory]
    [InlineData("☕")]
    [InlineData("👩‍💻")]
    [InlineData("👍🏽")]
    public void EmojiCellCannotChangeFollowingColumnPosition(string emoji)
    {
        QueryResult result = new()
        {
            Success = true,
            Columns = ["MemoryItems.Emoji", "MemoryItems.MomentType", "MemoryItems.TimelineId"],
            Rows = [[emoji, "0", "timeline-id"]],
            RowCount = 1,
        };

        ResultTable table = new() { Result = result };

        SelectableTextBlock momentHeader = FindCell(table, "MemoryItems.MomentType");
        SelectableTextBlock timelineHeader = FindCell(table, "MemoryItems.TimelineId");
        SelectableTextBlock moment = FindCell(table, "0");
        SelectableTextBlock timeline = FindCell(table, "timeline-id");

        table.Measure(new Size(2000, 2000));
        table.Arrange(new Rect(table.DesiredSize));

        Assert.Equal(Grid.GetColumn(momentHeader), Grid.GetColumn(moment));
        Assert.Equal(Grid.GetColumn(timelineHeader), Grid.GetColumn(timeline));
        Assert.Equal(momentHeader.Bounds.X, moment.Bounds.X);
        Assert.Equal(timelineHeader.Bounds.X, timeline.Bounds.X);
    }

    private static SelectableTextBlock FindCell(ResultTable table, string text) =>
        table.Children
            .OfType<SelectableTextBlock>()
            .Single(cell => cell.Text == text);
}
