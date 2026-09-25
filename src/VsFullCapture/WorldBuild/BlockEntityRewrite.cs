using Vintagestory.API.Datastructures;
using VsChunkDump.Core;

namespace VsFullCapture;

public readonly struct BlockEntityRewriteStats
{
    /// <summary>Block entities whose data had to be rewritten.</summary>
    public int Rewritten { get; init; }

    /// <summary>Entries in block entities (total).</summary>
    public int Total { get; init; }

    /// <summary>Palette of chiseled-block materials translated to codes.</summary>
    public int MaterialsAsCodes { get; init; }

    /// <summary>How many materials were not found in the server registry (they become rock).</summary>
    public int MaterialsUnknown { get; init; }

    /// <summary>Decor entries inside chiseled blocks translated by the id map.</summary>
    public int DecorIdsTranslated { get; init; }

    /// <summary>Failed to parse the TreeAttribute — the data was left as is.</summary>
    public int Failed { get; init; }
}

/// <summary>
/// Rewriting block entity data when moving to server data.
///
/// Chiseled blocks (that is, all custom "carved" blocks) store their shape and
/// materials not in the block but in the block entity, and the materials are
/// recorded as **block ids** of the server:
///   materials  int[]  — material block ids (BlockEntityMicroBlock)
///   decorIds   int[]  — decor block ids
///
/// The game's BlockEntityMicroBlock.MaterialIdsFromAttributes also understands a
/// second form: an array of **codes** (StringArrayAttribute), which it resolves
/// via GetBlock(new AssetLocation(code)). So we translate materials to codes —
/// then they are found in any world, even without a local registry. Decor has no
/// such variant, so it is translated by the id map.
/// </summary>
public static class BlockEntityRewrite
{
    public const string MaterialsKey = "materials";
    public const string DecorIdsKey = "decorIds";

    /// <summary>Game fallback if the material is missing from the server registry too.</summary>
    private const string FallbackMaterialCode = "rock-granite";

    public static byte[] Rewrite(
        BlockEntityPayload be,
        IReadOnlyDictionary<int, string>? serverRegistry,
        IReadOnlyDictionary<int, int>? idMap,
        out BlockEntityRewriteStats stats)
    {
        stats = new BlockEntityRewriteStats { Total = 1 };

        TreeAttribute tree;
        try
        {
            tree = new TreeAttribute();
            tree.FromBytes(be.Data);
        }
        catch (Exception)
        {
            stats = new BlockEntityRewriteStats { Total = 1, Failed = 1 };
            return be.Data;
        }

        int asCodes = 0, unknown = 0, decorTranslated = 0;

        // We only translate materials and decorIds for a chiseled block (class
        // MicroBlock): modded block entities may have attributes with the same
        // names, and rewriting "by attribute name" would corrupt them.
        bool isMicroBlock = IsMicroBlock(be.Classname);

        // Materials of a chiseled block: id → code.
        if (isMicroBlock && serverRegistry is { Count: > 0 } && tree[MaterialsKey] is IntArrayAttribute materials)
        {
            var codes = new string[materials.value.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                int id = materials.value[i];
                if (serverRegistry.TryGetValue(id, out string? code) && !string.IsNullOrEmpty(code))
                {
                    codes[i] = ShortCode(code);
                }
                else
                {
                    // There is no such id in the server registry (a gap) — take the
                    // same fallback block as the game itself does.
                    codes[i] = FallbackMaterialCode;
                    unknown++;
                }
            }
            tree[MaterialsKey] = new StringArrayAttribute(codes);
            asCodes = codes.Length;
        }

        // Decor inside the block (grass, flowers): id only, translated by the map.
        if (isMicroBlock && idMap is { Count: > 0 } && tree[DecorIdsKey] is IntArrayAttribute decors)
        {
            var translated = new int[decors.value.Length];
            for (int i = 0; i < translated.Length; i++)
            {
                int id = decors.value[i];
                translated[i] = idMap.TryGetValue(id, out int local) ? local : 0;
            }
            tree[DecorIdsKey] = new IntArrayAttribute(translated);
            decorTranslated = translated.Length;
        }

        if (asCodes == 0 && decorTranslated == 0)
        {
            // Nothing was changed — keep the original bytes so we do not touch the
            // data of block entities we do not understand.
            stats = new BlockEntityRewriteStats { Total = 1 };
            return be.Data;
        }

        stats = new BlockEntityRewriteStats
        {
            Total = 1,
            Rewritten = 1,
            MaterialsAsCodes = asCodes,
            MaterialsUnknown = unknown,
            DecorIdsTranslated = decorTranslated
        };
        return tree.ToBytes();
    }

    /// <summary>
    /// A chiseled block is the MicroBlock class. Modded block entities may have
    /// <c>materials</c>/<c>decorIds</c> attributes with a different meaning, so
    /// rewriting by attribute name without checking the class is not acceptable.
    /// </summary>
    private static bool IsMicroBlock(string? classname)
        => classname != null && classname.Contains("MicroBlock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Short form of the code, as the game itself writes it ("rock-granite" instead
    /// of "game:rock-granite"): that is exactly what MaterialIdsFromAttributes understands.
    /// </summary>
    private static string ShortCode(string code)
    {
        const string gamePrefix = "game:";
        return code.StartsWith(gamePrefix, StringComparison.Ordinal) ? code[gamePrefix.Length..] : code;
    }
}
