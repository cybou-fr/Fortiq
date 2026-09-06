using Avalonia.Automation;
using Avalonia.Controls;
using Fortiq.Desktop;

namespace Fortiq.Desktop.Tests;

/// <summary>
/// The names controls carry for anybody not reading the screen with their eyes.
/// </summary>
/// <remarks>
/// The interface is built in code, which means no designer ever sees an unlabelled control and
/// nothing prompts anybody to label one. A row reading "Back up now", "Prove", "Settings" is
/// unambiguous when you can see which of five identical rows it is in, and useless when you cannot.
/// </remarks>
public sealed class AccessibleTests
{
    [Fact]
    public void ANamedControlCarriesTheNameAndIsStillItself()
    {
        var button = new Button { Content = "Prove" };

        var returned = button.Named("Prove recovery for Documents");

        Assert.Same(button, returned);
        Assert.Equal("Prove recovery for Documents", AutomationProperties.GetName(button));
    }

    [Fact]
    public void DecorationIsKeptOutOfTheAccessibilityTree()
    {
        // The status dot beside "Needs attention" says the same thing in colour that the label says
        // in words. Announcing both is not thoroughness, it is the useful half being harder to find.
        var dot = new Border();

        dot.Decorative();

        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(dot));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyNameIsARefusalRatherThanALabelNobodyCanHear(string? name)
    {
        // A control named with whitespace reads as unlabelled, which is the bug this exists to stop -
        // arriving quietly, through a caller that interpolated something that turned out to be empty.
        Assert.ThrowsAny<ArgumentException>(() => new Button().Named(name!));
    }
}
