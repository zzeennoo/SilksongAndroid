using Mono.Cecil;

namespace BundleSurgery;

/// <summary>
/// Reports the owners of the System.IO types involved in the launch-time
/// PathInternal probe and rejects competing core File/FileStream definitions.
/// </summary>
internal static class SystemIoAudit
{
    private static readonly string[] SingleOwnerTypes =
    {
        "System.IO.File",
        "System.IO.FileStream",
    };

    private static readonly string[] MultiOwnerTypes =
    {
        // Unity's unityaot-linux profile intentionally carries private
        // PathInternal implementations in mscorlib, System and
        // System.IO.Compression.FileSystem. The generated-source audit must
        // enumerate all of them; treating them as competing core libraries is
        // itself a false positive.
        "System.IO.PathInternal",
    };

    internal static int Run(string assemblyDirectory)
    {
        if (!Directory.Exists(assemblyDirectory))
        {
            Console.Error.WriteLine($"[assembly-audit] directory does not exist: {assemblyDirectory}");
            return 1;
        }

        var watchedTypes = SingleOwnerTypes.Concat(MultiOwnerTypes).ToArray();
        var owners = watchedTypes.ToDictionary(type => type, _ => new List<string>());
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
        foreach (var type in watchedTypes)
        {
            var found = owners[type];
            Console.WriteLine(
                $"[assembly-audit] {type}: " +
                (found.Count == 0 ? "NO OWNER" : string.Join("; ", found)));
            if (SingleOwnerTypes.Contains(type) && found.Count != 1)
            {
                Console.Error.WriteLine(
                    $"[assembly-audit] expected exactly one implementation of {type}, found {found.Count}");
                failed = true;
            }
            else if (MultiOwnerTypes.Contains(type) && found.Count == 0)
            {
                Console.Error.WriteLine($"[assembly-audit] expected at least one implementation of {type}");
                failed = true;
            }
        }
        return failed ? 1 : 0;
    }
}
