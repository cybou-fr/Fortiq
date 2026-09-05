using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using static Fortiq.Desktop.DesignTokens;

namespace Fortiq.Desktop.Controls;

/// <summary>
/// Lays the recovery words out in a numbered grid, one word per cell.
/// </summary>
/// <remarks>
/// This is a separate, testable piece because of what happened when it was not one. The grid was
/// built inline with four <c>ColumnDefinitions</c> and no <c>RowDefinitions</c> at all - and a Grid
/// with no row definitions has exactly one implicit row, so <c>Grid.SetRow(1..5)</c> put every word
/// after the fourth on top of the words already in row 0. Twenty-four words were drawn into four
/// cells and only the last four could be read.
///
/// Nothing failed. The window rendered, the wizard advanced, and the verification step then asked for
/// word 4 - which had never been visible. Somebody following the instructions on screen would have
/// written down four words, believed they had their recovery phrase, and discovered otherwise on the
/// single day it matters.
///
/// A view that cannot be asked what it produced cannot be checked, and this is the one screen in the
/// product where a rendering mistake is unrecoverable. So the layout is computed here, where a test
/// can read the answer back.
/// </remarks>
public static class MnemonicWordGrid
{
    /// <summary>How many words sit on a row.</summary>
    public const int Columns = 4;

    /// <summary>Splits a mnemonic into its words, ignoring any spacing it arrives with.</summary>
    public static string[] Split(string? mnemonic) =>
        (mnemonic ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Rows needed to show <paramref name="wordCount"/> words, including a partial last row.</summary>
    public static int RowsFor(int wordCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wordCount);
        return (wordCount + Columns - 1) / Columns;
    }

    /// <summary>Builds the grid for <paramref name="mnemonic"/>, numbered from one.</summary>
    public static Grid Build(string? mnemonic)
    {
        var words = Split(mnemonic);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            RowSpacing = 8,
            ColumnSpacing = 8
        };

        // The rows have to be declared. Everything else here is arithmetic that was already correct;
        // this loop is the one whose absence made the screen unreadable.
        for (var row = 0; row < RowsFor(words.Length); row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var index = 0; index < words.Length; index++)
        {
            var cell = Cell(index + 1, words[index]);
            Grid.SetColumn(cell, index % Columns);
            Grid.SetRow(cell, index / Columns);
            grid.Children.Add(cell);
        }

        return grid;
    }

    private static Border Cell(int number, string word) => new()
    {
        Background = Surface,
        BorderBrush = Line,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Child = new TextBlock
        {
            Text = $"{number}.  {word}",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ink
        }
    };
}
