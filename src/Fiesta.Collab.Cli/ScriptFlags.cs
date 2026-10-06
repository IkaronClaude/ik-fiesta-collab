using System.Text;

namespace Fiesta.Collab.Cli;

/// <summary>
/// Scenario-script preprocessor (Fiesta2026on2016 ticket P3 "LUA / PS SCENARIO PREPROCESSOR", operator 2026-10-06):
/// an override script (.lua / .ps) may carry variant blocks in comments,
///     --#if REB_INSTANCE_COINS        (Lua; a .ps uses ;#if ...)
///     ... the variant's code ...
///     --#else
///     ... the stock code ...
///     --#endif
/// resolved at build time by the flags in FIESTA_SCRIPT_FLAGS (comma / space separated; empty = the stock build), so
/// one annotated copy serves every product and the parity build emits the stock script. `#if A|B` is true when any flag
/// is set, `#if !A` when A is not. Blocks nest. Directive lines are removed; every other byte (encoding, line ending) is
/// kept as it was.
/// </summary>
public static class ScriptFlags
{
    static readonly string[] Extensions = [".lua", ".ps"];

    public static HashSet<string> FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("FIESTA_SCRIPT_FLAGS") ?? "";
        return new HashSet<string>(raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                                   StringComparer.OrdinalIgnoreCase);
    }

    public static bool Applies(string path) =>
        Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves the file in place when it carries directives; returns whether it changed.</summary>
    public static bool ResolveFile(string path, HashSet<string> flags)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);           // byte-preserving: cp949 text passes through untouched
        if (!text.Contains("#if ", StringComparison.Ordinal)) return false;
        var outText = Resolve(text, flags, path);
        if (outText == text) return false;
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(outText));
        return true;
    }

    public static string Resolve(string text, HashSet<string> flags, string what = "")
    {
        var sb = new StringBuilder(text.Length);
        // stack of (this branch active, any branch of this block taken, parent active)
        var stack = new Stack<(bool active, bool taken, bool parent)>();
        bool active = true;
        int pos = 0, lineNo = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int end = nl < 0 ? text.Length : nl + 1;
            var line = text.Substring(pos, end - pos);
            pos = end;
            lineNo++;
            var d = Directive(line);
            if (d == null)
            {
                if (active) sb.Append(line);
                continue;
            }
            var (kind, arg) = d.Value;
            switch (kind)
            {
                case "if":
                    {
                        bool cond = Eval(arg, flags);
                        stack.Push((active && cond, cond, active));
                        active = active && cond;
                        break;
                    }
                case "else":
                    {
                        if (stack.Count == 0) throw new InvalidDataException($"{what}:{lineNo}: #else without #if");
                        var top = stack.Pop();
                        bool now = top.parent && !top.taken;
                        stack.Push((now, true, top.parent));
                        active = now;
                        break;
                    }
                case "endif":
                    {
                        if (stack.Count == 0) throw new InvalidDataException($"{what}:{lineNo}: #endif without #if");
                        var top = stack.Pop();
                        active = top.parent;
                        break;
                    }
            }
        }
        if (stack.Count != 0) throw new InvalidDataException($"{what}: #if without #endif");
        return sb.ToString();
    }

    static (string kind, string arg)? Directive(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("--")) t = t[2..].TrimStart();
        else if (t.StartsWith(';')) t = t[1..].TrimStart();
        else return null;
        if (t.StartsWith("#if ", StringComparison.Ordinal)) return ("if", t[4..].Trim());
        if (t == "#else" || t.StartsWith("#else ", StringComparison.Ordinal)) return ("else", "");
        if (t == "#endif" || t.StartsWith("#endif ", StringComparison.Ordinal)) return ("endif", "");
        return null;
    }

    static bool Eval(string expr, HashSet<string> flags)
    {
        foreach (var alt in expr.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = alt.Split([' ', '\t', ';'], 2)[0];       // a trailing comment after the flag is allowed
            bool neg = name.StartsWith('!');
            if (neg) name = name[1..];
            if (flags.Contains(name) != neg) return true;
        }
        return false;
    }
}
