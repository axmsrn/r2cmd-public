using System.IO;

namespace R2Cmd;

/// <summary>
/// Drive panel item. Name is what is displayed and handled
/// by legacy string logic ("C:", "D:", "NET"); Type determines the icon.
/// </summary>
public sealed class DriveItem
{
    public required string Name { get; init; }

    // Fixed / Removable / Network / CDRom / Ram / Unknown — determines XAML icon selection.
    public DriveType Type { get; init; }
}
