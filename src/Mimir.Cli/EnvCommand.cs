using Microsoft.Extensions.Logging;
using Mimir.Core.Project;

namespace Mimir.Cli;

/// <summary>
/// Handles all mimir env &lt;name&gt; &lt;verb&gt; logic.
/// Format: mimir env &lt;name|all&gt; &lt;verb&gt; [args...]
/// Verbs: init (name only), set, get, list, remove
/// </summary>
public static class EnvCommand
{
    private sealed record EnvProp(string Key, string Description, Func<EnvironmentConfig, string?> Get, Action<EnvironmentConfig, string> Set);

    private static readonly EnvProp[] Properties =
    [
        new("import-path",
            "Source directory to import data files from.",
            c => c.ImportPath,
            (c, v) => c.ImportPath = v),

        new("build-path",
            "Output directory for mimir build. Default: build/<name>",
            c => c.BuildPath,
            (c, v) => c.BuildPath = v),

        new("overrides-path",
            "Files copied verbatim into build output last, overriding tables and copyFile actions. Default: overrides/<name>",
            c => c.OverridesPath,
            (c, v) => c.OverridesPath = v),

        new("seed-pack-baseline",
            "true/false. Hash importable source files after import to establish the pack diff baseline. Enable for packable (client) envs; leave false for server envs.",
            c => c.SeedPackBaseline ? "true" : "false",
            (c, v) =>
            {
                if (!bool.TryParse(v, out var b))
                    throw new ArgumentException($"Value for seed-pack-baseline must be true or false, got: {v}");
                c.SeedPackBaseline = b;
            }),
    ];

    public static Task HandleAsync(string projectDir, IReadOnlyList<string> tokens, ILogger logger)
    {
        if (tokens.Count == 0)
        {
            PrintUsage(projectDir);
            return Task.CompletedTask;
        }

        var envNameArg = tokens[0];

        if (tokens.Count == 1)
        {
            // mimir env <name> — show list for that env (or all envs if "all")
            if (envNameArg == "all")
            {
                foreach (var n in EnvironmentStore.ListNames(projectDir))
                    DoList(projectDir, n);
            }
            else
            {
                DoList(projectDir, envNameArg);
            }
            return Task.CompletedTask;
        }

        var verb = tokens[1].ToLowerInvariant();
        var rest = tokens.Skip(2).ToList();

        if (verb == "init")
        {
            if (envNameArg == "all")
            {
                logger.LogError("'all' is not supported for init. Specify a name: mimir env <name> init");
                return Task.CompletedTask;
            }
            DoInit(projectDir, envNameArg, rest, logger);
            return Task.CompletedTask;
        }

        // All other verbs support "all"
        IReadOnlyList<string> targets = envNameArg == "all"
            ? EnvironmentStore.ListNames(projectDir)
            : [envNameArg];

        if (envNameArg == "all" && targets.Count == 0)
        {
            logger.LogWarning("No environments configured. Use: mimir env <name> init");
            return Task.CompletedTask;
        }

        foreach (var name in targets)
        {
            switch (verb)
            {
                case "set":    DoSet(projectDir, name, rest, logger); break;
                case "get":    DoGet(projectDir, name, rest, logger); break;
                case "list":   DoList(projectDir, name); break;
                case "remove": DoRemove(projectDir, name, logger); break;
                default:
                    logger.LogError("Unknown verb '{Verb}'. Valid verbs: init, set, get, list, remove", verb);
                    return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    private static void DoInit(string projectDir, string envName, List<string> args, ILogger logger)
    {
        if (EnvironmentStore.Exists(projectDir, envName))
        {
            logger.LogError("Environment '{Name}' already exists. Use 'mimir env {Name} set' to modify it.", envName, envName);
            return;
        }

        string? importPath = null;
        bool patchable = false;
        foreach (var arg in args)
        {
            if (arg.Equals("--patchable", StringComparison.OrdinalIgnoreCase))
                patchable = true;
            else if (!arg.StartsWith('-'))
                importPath = arg;
        }

        var config = new EnvironmentConfig
        {
            ImportPath = importPath,
            BuildPath = $"build/{envName}",
            OverridesPath = $"overrides/{envName}",
            SeedPackBaseline = patchable
        };
        EnvironmentStore.Save(projectDir, envName, config);

        logger.LogInformation("Created environment '{Name}'", envName);
        logger.LogInformation("  import-path       = {V}", importPath ?? "(unset — use: mimir env {N} set import-path <path>)");
        logger.LogInformation("  build-path        = {V}", config.BuildPath);
        logger.LogInformation("  overrides-path    = {V}", config.OverridesPath);
        logger.LogInformation("  seed-pack-baseline= {V}", patchable ? "true" : "false");
    }

    private static void DoSet(string projectDir, string envName, List<string> args, ILogger logger)
    {
        if (!EnvironmentStore.Exists(projectDir, envName))
        {
            logger.LogError("Environment '{Name}' does not exist. Use 'mimir env {Name} init' first.", envName, envName);
            return;
        }
        if (args.Count < 2)
        {
            logger.LogError("Usage: mimir env {Name} set <key> <value>", envName);
            return;
        }

        var key = args[0].ToLowerInvariant();
        var value = args[1];
        var prop = Properties.FirstOrDefault(p => p.Key == key);
        if (prop == null)
        {
            logger.LogError("Unknown property '{Key}'. Valid keys: {Keys}", key, string.Join(", ", Properties.Select(p => p.Key)));
            return;
        }

        var config = EnvironmentStore.Load(projectDir, envName)!;
        try
        {
            prop.Set(config, value);
        }
        catch (ArgumentException ex)
        {
            logger.LogError("{Message}", ex.Message);
            return;
        }

        EnvironmentStore.Save(projectDir, envName, config);
        logger.LogInformation("Set {Name}.{Key} = {Value}", envName, key, value);
    }

    private static void DoGet(string projectDir, string envName, List<string> args, ILogger logger)
    {
        if (!EnvironmentStore.Exists(projectDir, envName))
        {
            logger.LogError("Environment '{Name}' does not exist.", envName);
            return;
        }
        if (args.Count < 1)
        {
            logger.LogError("Usage: mimir env {Name} get <key>", envName);
            return;
        }

        var key = args[0].ToLowerInvariant();
        var prop = Properties.FirstOrDefault(p => p.Key == key);
        if (prop == null)
        {
            logger.LogError("Unknown property '{Key}'.", key);
            return;
        }

        var config = EnvironmentStore.Load(projectDir, envName)!;
        Console.WriteLine(prop.Get(config) ?? "(unset)");
    }

    private static void DoList(string projectDir, string envName)
    {
        var exists = EnvironmentStore.Exists(projectDir, envName);
        var config = exists ? EnvironmentStore.Load(projectDir, envName) : null;

        var keyWidth = Properties.Max(p => p.Key.Length);
        Console.WriteLine($"[{envName}]{(exists ? "" : "  (not configured — run: mimir env " + envName + " init)")}");
        foreach (var prop in Properties)
        {
            var value = config != null ? (prop.Get(config) ?? "(unset)") : "(unset)";
            Console.WriteLine($"  {prop.Key.PadRight(keyWidth)}  {value}");
            Console.WriteLine($"  {new string(' ', keyWidth)}  {prop.Description}");
        }
        Console.WriteLine();
    }

    private static void DoRemove(string projectDir, string envName, ILogger logger)
    {
        if (!EnvironmentStore.Exists(projectDir, envName))
        {
            logger.LogError("Environment '{Name}' does not exist.", envName);
            return;
        }
        EnvironmentStore.Remove(projectDir, envName);
        logger.LogInformation("Removed environment '{Name}'.", envName);
    }

    private static void PrintUsage(string projectDir)
    {
        Console.WriteLine("Usage: mimir env <name|all> <verb> [args]");
        Console.WriteLine();
        Console.WriteLine("Verbs:");
        Console.WriteLine("  init [importPath] [--patchable]   Create environment (init only, not all)");
        Console.WriteLine("  set <key> <value>                 Set a property");
        Console.WriteLine("  get <key>                         Print a single property value");
        Console.WriteLine("  list                              List all properties with descriptions");
        Console.WriteLine("  remove                            Delete the environment config");
        Console.WriteLine();
        var names = EnvironmentStore.ListNames(projectDir);
        if (names.Count > 0)
        {
            Console.WriteLine("Configured environments:");
            foreach (var n in names)
            {
                var cfg = EnvironmentStore.Load(projectDir, n);
                Console.WriteLine($"  {n,-16} import-path={cfg?.ImportPath ?? "(unset)"}");
            }
        }
        else
        {
            Console.WriteLine("No environments configured. Run: mimir env <name> init [importPath]");
        }
    }
}
