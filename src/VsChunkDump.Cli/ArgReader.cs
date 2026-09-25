using VsChunkDump.Core;

namespace VsChunkDump.Cli;

/// <summary>
/// Very simple argument parsing: no external dependencies, so the tool
/// can be built and run offline.
/// </summary>
public sealed class ArgReader
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);

    public static ArgReader Parse(ReadOnlySpan<string> args)
    {
        var r = new ArgReader();
        for (int i = 0; i < args.Length; i++)
        {
            string tok = args[i];
            if (IsOptionToken(tok))
            {
                string name = tok;
                string? value = null;

                int eq = tok.IndexOf('=');
                if (eq > 0)
                {
                    name = tok[..eq];
                    value = tok[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !IsOptionToken(args[i + 1]))
                {
                    value = args[++i];
                }
                r._options[name] = value ?? "true";
            }
            else
            {
                r._positional.Add(tok);
            }
        }
        return r;
    }

    /// <summary>An option is a token like <c>-o</c> or <c>--out</c>; <c>-100,5</c> is a value, not an option.</summary>
    private static bool IsOptionToken(string s)
    {
        if (s.Length < 2 || s[0] != '-') return false;
        int i = s[1] == '-' ? 2 : 1;
        return i < s.Length && char.IsLetter(s[i]);
    }

    public string Positional(int index) => index < _positional.Count ? _positional[index] : "";

    public int PositionalCount => _positional.Count;

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name)
        => _options.TryGetValue(name, out string? v) ? v : null;

    public string Require(string name, string? alias = null)
    {
        string? v = Get(name) ?? (alias != null ? Get(alias) : null);
        if (v == null || v == "true")
            throw new ArgumentException($"Required parameter {name} is not set");
        return v;
    }

    public int Int(string name, int fallback, string? alias = null)
    {
        string? v = Get(name) ?? (alias != null ? Get(alias) : null);
        if (v == null) return fallback;
        if (!int.TryParse(v, out int parsed))
            throw new ArgumentException($"Parameter {name} must be an integer, got \"{v}\"");
        return parsed;
    }

    /// <summary>Parse <c>--area x0,z0,x1,z1</c> (in blocks, inclusive).</summary>
    public BlockBounds? Area()
    {
        string? v = Get("--area");
        if (v == null) return null;
        var parts = v.Split([',', ':'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new ArgumentException("--area expects four numbers: x0,z0,x1,z1");
        int[] n = parts.Select(p => int.Parse(p)).ToArray();
        return new BlockBounds(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
    }
}
