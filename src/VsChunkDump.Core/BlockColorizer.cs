namespace VsChunkDump.Core;

/// <summary>
/// Approximate coloring of Vintage Story blocks by their code.
/// The game only exposes exact textures from inside itself, so for offline preview
/// a table of heuristics over code substrings is used (rule order matters:
/// the first match wins). This is a deliberate simplification: the color is only
/// needed for the offline PNG preview, not for in-game rendering.
/// </summary>
public static class BlockColorizer
{
    private static readonly (byte R, byte G, byte B, byte A) Unknown = (120, 120, 130, 255);
    private static readonly (byte R, byte G, byte B, byte A) Air = (0, 0, 0, 0);

    private static readonly (string Key, byte R, byte G, byte B, byte A)[] Rules =
    [
        ("air", 0, 0, 0, 0),
        ("water", 48, 110, 190, 190),
        ("lava", 207, 80, 20, 255),
        ("ice", 170, 205, 225, 220),
        ("glacier", 190, 215, 230, 255),
        ("snow", 240, 244, 250, 255),
        ("obsidian", 35, 30, 45, 255),
        ("basalt", 66, 62, 60, 255),
        ("granite", 150, 110, 105, 255),
        ("andesite", 110, 112, 112, 255),
        ("peridotite", 95, 120, 100, 255),
        ("kimberlite", 80, 95, 100, 255),
        ("limestone", 200, 190, 165, 255),
        ("chalk", 230, 228, 220, 255),
        ("sandstone", 214, 195, 145, 255),
        ("slate", 92, 96, 100, 255),
        ("shale", 106, 100, 95, 255),
        ("claystone", 150, 145, 140, 255),
        ("halite", 235, 235, 242, 255),
        ("quartz", 226, 226, 232, 255),
        ("olivine", 140, 172, 92, 255),
        ("sulfur", 214, 210, 110, 255),
        ("coal", 50, 50, 55, 255),
        ("charcoal", 45, 45, 48, 255),
        ("salt", 235, 235, 242, 255),
        ("sand", 222, 205, 150, 255),
        ("gravel", 140, 135, 130, 255),
        ("cob", 126, 120, 114, 255),
        ("mud", 92, 74, 58, 255),
        ("peat", 70, 56, 42, 255),
        ("soil", 122, 94, 64, 255),
        ("dirt", 116, 88, 60, 255),
        ("terra", 150, 96, 76, 255),
        ("farmland", 118, 88, 58, 255),
        ("compost", 84, 66, 46, 255),
        ("wood", 108, 80, 52, 255),
        ("log", 108, 80, 52, 255),
        ("debarked", 176, 142, 96, 255),
        ("plank", 172, 140, 96, 255),
        ("board", 172, 140, 96, 255),
        ("thatch", 190, 166, 100, 255),
        ("bamboo", 160, 176, 90, 255),
        ("leaves", 62, 122, 46, 255),
        ("needles", 52, 100, 60, 255),
        ("grass", 92, 152, 62, 255),
        ("fern", 74, 132, 58, 255),
        ("flower", 168, 150, 190, 255),
        ("crop", 142, 180, 72, 255),
        ("reed", 128, 158, 88, 255),
        ("papyrus", 140, 168, 96, 255),
        ("brick", 152, 82, 66, 255),
        ("glass", 205, 228, 238, 170),
        ("cloth", 200, 195, 180, 255),
        ("linen", 206, 200, 184, 255),
        ("wool", 214, 208, 194, 255),
        ("leather", 132, 96, 62, 255),
        ("metal", 158, 163, 170, 255),
        ("iron", 158, 163, 170, 255),
        ("steel", 122, 128, 138, 255),
        ("stainless", 196, 206, 212, 255),
        ("meteoric", 110, 116, 126, 255),
        ("copper", 186, 112, 68, 255),
        ("gold", 216, 180, 60, 255),
        ("silver", 216, 216, 222, 255),
        ("tin", 200, 200, 206, 255),
        ("lead", 96, 100, 112, 255),
        ("zinc", 176, 182, 186, 255),
        ("bismuth", 190, 160, 200, 255),
        ("chromium", 202, 212, 216, 255),
        ("titanium", 200, 200, 205, 255),
        ("ore", 120, 118, 118, 255),
        ("lantern", 232, 196, 110, 255),
        ("torch", 236, 170, 80, 255),
        ("path", 152, 142, 122, 255),
        ("road", 140, 132, 118, 255),
        ("mortar", 168, 162, 150, 255),
        ("plaster", 224, 220, 208, 255),
        ("hay", 198, 174, 96, 255),
        ("crate", 158, 128, 84, 255),
        ("barrel", 140, 110, 70, 255),
        ("chest", 150, 118, 74, 255),
        ("rock", 140, 140, 140, 255),
        ("stone", 140, 140, 140, 255),
        ("bone", 226, 222, 206, 255),
        ("resin", 202, 150, 60, 255),
        ("honey", 214, 158, 52, 255),
    ];

    /// <summary>Some wood species differ, so they are checked before the generic 'wood'.</summary>
    private static readonly (string Key, byte R, byte G, byte B)[] WoodTints =
    [
        ("birch", 216, 204, 176),
        ("oak", 158, 122, 76),
        ("maple", 176, 138, 96),
        ("walnut", 108, 78, 54),
        ("ebony", 62, 48, 42),
        ("purpleheart", 108, 66, 92),
        ("larch", 170, 128, 84),
        ("pine", 146, 108, 70),
        ("acacia", 178, 122, 72),
        ("kapok", 186, 150, 110),
        ("baldcypress", 150, 110, 78),
        ("redwood", 138, 78, 62),
        ("tualang", 168, 140, 108),
    ];

    /// <summary>Block color by its code (for example <c>game:rock-granite</c>).</summary>
    public static (byte R, byte G, byte B, byte A) ColorOf(string code)
    {
        if (string.IsNullOrEmpty(code)) return Unknown;

        string c = code;
        int colon = c.IndexOf(':');
        string path = colon >= 0 ? c[(colon + 1)..] : c;
        path = path.ToLowerInvariant();

        if (path is "air" or "airblock") return Air;

        // Wood: narrow down the species first.
        if (path.Contains("log") || path.Contains("wood") || path.Contains("plank") || path.Contains("debarked"))
        {
            foreach (var (key, r, g, b) in WoodTints)
            {
                if (path.Contains(key))
                {
                    // Planks/logs are lighter than raw wood.
                    if (path.Contains("plank") || path.Contains("debarked")) return ((byte)Math.Min(255, r + 22), (byte)Math.Min(255, g + 20), (byte)Math.Min(255, b + 18), (byte)255);
                    return (r, g, b, 255);
                }
            }
        }

        foreach (var rule in Rules)
        {
            if (path.Contains(rule.Key)) return (rule.R, rule.G, rule.B, rule.A);
        }

        // Stable pseudo-random placeholder color so different unknown blocks look distinct.
        uint h = 2166136261u;
        foreach (char ch in path) { h = (h ^ ch) * 16777619u; }
        byte rr = (byte)(90 + (h & 0x3F));
        byte gg = (byte)(90 + ((h >> 6) & 0x3F));
        byte bb = (byte)(90 + ((h >> 12) & 0x3F));
        return (rr, gg, bb, 255);
    }

    public static (byte R, byte G, byte B, byte A) ColorOf(int blockId, BlockTable table)
        => ColorOf(table.Code(blockId));

    /// <summary>Multiply a color by a brightness factor (for height/face shading).</summary>
    public static (byte R, byte G, byte B, byte A) Shade((byte R, byte G, byte B, byte A) c, float f)
    {
        return (
            (byte)Math.Clamp(c.R * f, 0, 255),
            (byte)Math.Clamp(c.G * f, 0, 255),
            (byte)Math.Clamp(c.B * f, 0, 255),
            c.A);
    }
}
