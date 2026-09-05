using Avalonia.Controls;
using Fortiq.Desktop.Controls;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// That every recovery word is readable on the screen that shows them.
/// </summary>
/// <remarks>
/// This is the one screen in the product where a rendering mistake cannot be undone. The mnemonic is
/// shown once, and what the person copies onto paper is the only way back to their data if the
/// machine is gone.
///
/// It was wrong. The grid declared four columns and no rows, and a Grid with no row definitions has a
/// single implicit row - so every word after the fourth was placed on top of one already there.
/// Twenty-four words occupied four cells and only the last four were legible. Nothing threw, nothing
/// logged, the wizard advanced normally, and the verification step then asked for word 4, which had
/// never appeared. The instructions on screen were followed exactly and produced a useless backup of
/// four words.
///
/// No test existed because no view was testable. These assertions are about placement, not looks.
/// </remarks>
public sealed class MnemonicWordGridTests
{
    private const string TwentyFourWords =
        "abandon ability able about above absent absorb abstract absurd abuse access accident " +
        "account accuse achieve acid acoustic acquire across act action actor actress actual";

    [Fact]
    public void EveryWordGetsACellOfItsOwn()
    {
        var grid = MnemonicWordGrid.Build(TwentyFourWords);

        var occupied = grid.Children
            .Select(child => (Row: Grid.GetRow(child), Column: Grid.GetColumn(child)))
            .ToList();

        Assert.Equal(24, occupied.Count);

        // Worth having, and worth knowing what it does not do: reintroducing the original defect
        // leaves this test passing. Grid.GetRow returns whatever was set on the child whether or not
        // a row exists to receive it, so the placements still look distinct here while the screen
        // shows four cells. The tests below are the ones that fail on the broken grid - verified by
        // deleting the row loop and watching exactly those four go red.
        Assert.Equal(24, occupied.Distinct().Count());
    }

    [Fact]
    public void TheGridDeclaresEnoughRowsToHoldThem()
    {
        var grid = MnemonicWordGrid.Build(TwentyFourWords);

        // One of the four assertions that actually catch the defect. Without these rows,
        // Grid.SetRow(1..5) is accepted by the child and ignored at layout, and everything collapses
        // into row 0. The row indexes were always right; there was nowhere for them to go.
        Assert.Equal(6, grid.RowDefinitions.Count);
        Assert.Equal(4, grid.ColumnDefinitions.Count);
    }

    [Fact]
    public void NoWordIsPlacedInARowThatDoesNotExist()
    {
        var grid = MnemonicWordGrid.Build(TwentyFourWords);

        foreach (var child in grid.Children)
        {
            Assert.InRange(Grid.GetRow(child), 0, grid.RowDefinitions.Count - 1);
            Assert.InRange(Grid.GetColumn(child), 0, grid.ColumnDefinitions.Count - 1);
        }
    }

    [Fact]
    public void WordsReadLeftToRightThenDown()
    {
        var grid = MnemonicWordGrid.Build(TwentyFourWords);
        var words = MnemonicWordGrid.Split(TwentyFourWords);

        // Order matters as much as presence: a phrase written down in the wrong order is as useless
        // as one missing a word, and the person copying it reads the grid the way they read a page.
        for (var index = 0; index < words.Length; index++)
        {
            var child = grid.Children[index];
            Assert.Equal(index / 4, Grid.GetRow(child));
            Assert.Equal(index % 4, Grid.GetColumn(child));
        }
    }

    [Fact]
    public void EachCellCarriesItsNumberAndItsWord()
    {
        var grid = MnemonicWordGrid.Build(TwentyFourWords);
        var words = MnemonicWordGrid.Split(TwentyFourWords);

        // Numbered from one, because the verification step asks for "word 4" and a person counting
        // from a grid numbered from zero would give the wrong answer to a correct question.
        for (var index = 0; index < words.Length; index++)
        {
            var text = ((TextBlock)((Border)grid.Children[index]).Child!).Text;

            Assert.StartsWith($"{index + 1}.", text, StringComparison.Ordinal);
            Assert.EndsWith(words[index], text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(12, 3)]
    [InlineData(24, 6)]
    public void APartialLastRowStillGetsARow(int wordCount, int expectedRows)
    {
        // A twelve-word mnemonic, or any length that does not divide by four, must not lose its
        // remainder. The count is not fixed anywhere in this class on purpose.
        Assert.Equal(expectedRows, MnemonicWordGrid.RowsFor(wordCount));
    }

    [Fact]
    public void AShorterMnemonicIsLaidOutJustAsCompletely()
    {
        var twelve = string.Join(' ', MnemonicWordGrid.Split(TwentyFourWords).Take(12));

        var grid = MnemonicWordGrid.Build(twelve);

        Assert.Equal(12, grid.Children.Count);
        Assert.Equal(3, grid.RowDefinitions.Count);
        Assert.Equal(12, grid.Children.Select(c => (Grid.GetRow(c), Grid.GetColumn(c))).Distinct().Count());
    }

    [Fact]
    public void AnEmptyMnemonicProducesAnEmptyGridRatherThanThrowing()
    {
        // Reached if the view renders before the model has a mnemonic. Showing nothing is correct;
        // crashing on the recovery-phrase step is not.
        var grid = MnemonicWordGrid.Build(null);

        Assert.Empty(grid.Children);
        Assert.Empty(grid.RowDefinitions);
    }

    [Fact]
    public void ExtraSpacingInTheMnemonicDoesNotCreateEmptyCells()
    {
        var grid = MnemonicWordGrid.Build("  alpha   beta  gamma ");

        Assert.Equal(3, grid.Children.Count);
        Assert.Single(grid.RowDefinitions);
    }
}
