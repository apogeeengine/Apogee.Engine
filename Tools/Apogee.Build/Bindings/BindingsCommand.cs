using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Apogee.Build.Bindings;

/// <summary><c>./build.sh bindings [name…]</c>: regenerates <c>Generated/</c> for the named libraries, or for all of them.</summary>
public static class BindingsCommand
{
    public static int Run(string repositoryRoot, string[] names)
    {
        string configs = Path.Combine(AppContext.BaseDirectory, "Bindings", "Configs");
        var all = Directory.GetFiles(configs, "*.json").Order(StringComparer.Ordinal).Select(BindingConfig.Load).ToList();
        var selected = names.Length == 0 ? all : all.Where(c => names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
        {
            Console.Error.WriteLine($"No such library. Known: {string.Join(", ", all.Select(c => c.Name))}");
            return 1;
        }

        foreach (BindingConfig config in selected)
        {
            Console.WriteLine($"{config.Name}: parsing…");
            ApiModel model = config.Kind switch
            {
                "vulkan" => new VulkanRegistryParser(config, repositoryRoot).Parse(),
                "steam" => new SteamApiParser(config, repositoryRoot).Parse(),
                _ => new HeaderParser(config, repositoryRoot).Parse(),
            };

            string output = Path.Combine(repositoryRoot, config.Project, "Generated");
            Directory.CreateDirectory(output);
            foreach ((string file, string contents) in CSharpEmitter.Emit(config, model))
            {
                File.WriteAllText(Path.Combine(output, file), contents);
            }

            string report = Path.Combine(repositoryRoot, "Bin", "bindings", config.Name + ".skipped.txt");
            File.WriteAllLines(report, model.Skipped.Order(StringComparer.Ordinal));
            Console.WriteLine($"{config.Name}: {model.Functions.Count} functions, {model.Structs.Count} structs, "
                + $"{model.Enums.Count} enums, {model.Constants.Count} constants; {model.Skipped.Count} skipped (see {Path.GetRelativePath(repositoryRoot, report)})");
        }

        return 0;
    }
}
