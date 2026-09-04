using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Fortiq.Desktop;

/// <summary>The single bundled Fortiq identity used by Windows chrome and in-product branding.</summary>
internal static class FortiqBrand
{
    private static readonly Uri IconUri = new("avares://Fortiq.Desktop/Assets/icon.ico");
    private static readonly Uri LogoUri = new("avares://Fortiq.Desktop/Assets/icon.png");

    internal static WindowIcon WindowIcon()
    {
        using var stream = AssetLoader.Open(IconUri);
        return new WindowIcon(stream);
    }

    internal static Bitmap Logo()
    {
        using var stream = AssetLoader.Open(LogoUri);
        return new Bitmap(stream);
    }
}
