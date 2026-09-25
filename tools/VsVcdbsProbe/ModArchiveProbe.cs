using System.IO.Compression;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace VsVcdbsProbe;

/// <summary>
/// Verify a finished mod archive with the game's REAL mod loader.
///
/// We do not trust our own packaging: the archive is unpacked and parsed by the
/// <see cref="ModContainer"/> class from Vintagestory.Common, that is, by exactly the
/// code the game runs at startup. If everything matches here, both the game and
/// ModDB will accept the archive — they both look at the same structure.
///
/// The structure rules come from the 1.22.7 decompile (ModContainer.Unpack/LoadModInfo):
///   * modinfo.json must live IN THE ROOT of the archive (ZipEntry "modinfo.json");
///   * .dll files are only allowed in the root, no nested folders except native/;
///   * .cs files are only allowed under src/, otherwise the mod is marked as failed to load.
/// </summary>
public static class ModArchiveProbe
{
    public static int Run(IReadOnlyList<string> args)
    {
        var zips = new List<string>();
        string expectModId = null;
        string expectVersion = null;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--modid":
                    expectModId = args[++i];
                    break;
                case "--version":
                    expectVersion = args[++i];
                    break;
                default:
                    zips.Add(args[i]);
                    break;
            }
        }

        if (zips.Count == 0)
        {
            Console.WriteLine("usage: VsVcdbsProbe modzip <archive.zip> [--modid <id>] [--version <version>]");
            return 1;
        }

        int failures = 0;
        foreach (string zip in zips)
        {
            Console.WriteLine("--- archive " + zip + " ---");
            failures += CheckOne(zip, expectModId, expectVersion);
            Console.WriteLine();
        }
        return failures == 0 ? 0 : 1;
    }

    private static int CheckOne(string zipPath, string expectModId, string expectVersion)
    {
        int failures = 0;

        if (!File.Exists(zipPath))
        {
            Console.WriteLine("  FAIL: no such file");
            return 1;
        }

        var file = new FileInfo(zipPath);
        Console.WriteLine($"  file: {file.Length} bytes, sha256 {Sha256(zipPath)[..16]}…");

        // ---- 1. archive structure: the same rules ModContainer applies ----
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            bool hasModInfo = false;
            int rootDlls = 0;

            foreach (var entry in archive.Entries)
            {
                string rel = entry.FullName.Replace('\\', '/');
                if (rel.EndsWith('/'))
                {
                    continue; // directory
                }
                if (rel.StartsWith('/') || rel.Contains(".."))
                {
                    Console.WriteLine($"  FAIL: invalid path in the archive: {rel}");
                    failures++;
                    continue;
                }

                string top = rel.Contains('/') ? rel[..rel.IndexOf('/')] : null;
                string ext = Path.GetExtension(rel).ToUpperInvariant();

                if (rel == "modinfo.json")
                {
                    hasModInfo = true;
                }
                else if (ext == ".DLL")
                {
                    if (top == null)
                    {
                        rootDlls++;
                    }
                    else if (top != "native")
                    {
                        // ModContainer: "File '...' is not in the mod's root folder. Won't load this mod."
                        Console.WriteLine($"  FAIL: DLL is neither in the archive root nor under native/: {rel}");
                        failures++;
                    }
                }
                else if (ext == ".CS" && top != "src")
                {
                    // ModContainer: a load error if .cs is not under src/
                    Console.WriteLine($"  FAIL: .cs outside the src/ folder: {rel}");
                    failures++;
                }
            }

            if (!hasModInfo)
            {
                Console.WriteLine("  FAIL: modinfo.json was not found in the archive root");
                failures++;
            }
            if (rootDlls == 0)
            {
                Console.WriteLine("  FAIL: there is no .dll in the archive root");
                failures++;
            }
            Console.WriteLine($"  structure: modinfo.json in root — {(hasModInfo ? "yes" : "no")}, dlls in root — {rootDlls}");
        }

        // ---- 2. parse the archive with the game's code ----
        string unpackDir = Path.Combine(Path.GetTempPath(), "vsmodzip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var container = new ModContainer(new FileInfo(zipPath), new NullLogger(), false);
            Console.WriteLine($"  SourceType: {container.SourceType} (ZIP expected)");
            if (container.SourceType != EnumModSourceType.ZIP)
            {
                failures++;
            }

            container.Unpack(unpackDir);
            using (var loader = new ModAssemblyLoader(Array.Empty<string>(), Array.Empty<ModContainer>()))
            {
                container.LoadModInfo(null, loader);
            }

            if (container.Info == null)
            {
                Console.WriteLine("  FAIL: the game could not read modinfo.json (Info == null)");
                return failures + 1;
            }

            var info = container.Info;
            Console.WriteLine($"  ModInfo: modid={info.ModID} version={info.Version} name=\"{info.Name}\" " +
                              $"type={info.Type} side={info.Side}");

            if (string.IsNullOrEmpty(info.ModID))
            {
                Console.WriteLine("  FAIL: modid is empty");
                failures++;
            }
            if (expectModId != null && info.ModID != expectModId)
            {
                Console.WriteLine($"  FAIL: modid in the archive is \"{info.ModID}\", in the source \"{expectModId}\"");
                failures++;
            }
            if (expectVersion != null && info.Version != expectVersion)
            {
                Console.WriteLine($"  FAIL: version in the archive is \"{info.Version}\", in the source \"{expectVersion}\"");
                failures++;
            }

            // ---- 3. assemblies: file name == assembly name, all in the root ----
            Console.WriteLine($"  assemblies found: {container.AssemblyFiles.Count}");
            foreach (string asm in container.AssemblyFiles)
            {
                string rel = Path.GetRelativePath(container.FolderPath, asm);
                bool atRoot = !rel.Contains(Path.DirectorySeparatorChar);
                string name;
                try
                {
                    name = AssemblyName.GetAssemblyName(asm).Name;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"  FAIL: {rel} — not a managed assembly ({e.GetType().Name})");
                    failures++;
                    continue;
                }

                bool nameMatches = name == Path.GetFileNameWithoutExtension(rel);
                Console.WriteLine($"    {rel} ({new FileInfo(asm).Length} bytes), AssemblyName={name}" +
                                  (atRoot && nameMatches ? "" : "  <-- FAIL"));
                if (!atRoot || !nameMatches)
                {
                    failures++;
                }
            }

            if (container.AssemblyFiles.Count == 0)
            {
                Console.WriteLine("  FAIL: the game found no assemblies in the mod");
                failures++;
            }
            if (container.SourceFiles.Count > 0)
            {
                Console.WriteLine($"  WARNING: the game found {container.SourceFiles.Count} .cs files — the mod will be compiled on the fly");
            }
            if (container.Error != null)
            {
                Console.WriteLine($"  FAIL: the loader reported an error {container.Error}");
                failures++;
            }

            Console.WriteLine($"  Status: {container.Status}");
        }
        catch (Exception e)
        {
            Console.WriteLine("  FAIL: the game's mod loader threw an exception: " + e.Message);
            failures++;
        }
        finally
        {
            try { Directory.Delete(unpackDir, recursive: true); } catch { /* temporary directory */ }
        }

        Console.WriteLine(failures == 0
            ? "  OK: the archive was accepted by the game's real ModContainer"
            : $"  FAIL: {failures} issue(s)");
        return failures;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
}
