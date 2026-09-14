using Mono.Cecil;

namespace BundleSurgery;

/// <summary>
/// Verifies that the exact assembly graph handed to IL2CPP has one owner for
/// each System.IO type involved in the launch-time PathInternal probe.
/// </summary>
internal static class SystemIoAudit
{
    private static readonly string[] WatchedTypes =
    {
        "System.IO.File",
        "System.IO.FileStream",
        "System.IO.PathInternal",
    };

    internal static int Run(string assemblyDirectory)
    {
        if (!Directory.Exists(assemblyDirectory))
        {
            Console.Error.WriteLine($"[assembly-audit] directory does not exist: {assemblyDirectory}");
            return 1;
        }

        var owners = WatchedTypes.ToDictionary(type => type, _ => new List<string>());
        var assemblies = Directory.GetFiles(assemblyDirectory, "*.dll")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var path in assemblies)
        {
            try
            {
                using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false,
                });
                foreach (var type in assembly.MainModule.Types)
                {
                    if (owners.TryGetValue(type.FullName, out var found))
                    {
                        found.Add($"{assembly.Name.FullName} [{Path.GetFileName(path)}]");
                    }
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    $"[assembly-audit] could not inspect {Path.GetFileName(path)}: {error.Message}");
                return 1;
            }
        }

        var failed = false;
        Console.WriteLine($"[assembly-audit] inspected {assemblies.Length} staged assemblies");
        foreach (var type in WatchedTypes)
        {
            var found = owners[type];
            Console.WriteLine(
                $"[assembly-audit] {type}: " +
                (found.Count == 0 ? "NO OWNER" : string.Join("; ", found)));
            if (found.Count != 1)
            {
                Console.Error.WriteLine(
                    $"[assembly-audit] expected exactly one implementation of {type}, found {found.Count}");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }
}
