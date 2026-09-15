using Mono.Cecil;
using Mono.Cecil.Cil;

namespace BundleSurgery;

/// <summary>
/// Replaces every unityaot System.IO.PathInternal.GetIsCaseSensitive probe
/// with the method's own conservative fallback: case-insensitive.
///
/// The stock implementation creates a temporary FileStream and catches any
/// exception before returning false.  In the affected Android IL2CPP build,
/// the platform-specific FileStream constructor is an unsupported-method
/// stub whose exception escapes the static initializer and aborts the game.
/// Returning false directly preserves the documented failure behaviour while
/// removing filesystem I/O from class initialization.
/// </summary>
internal static class PatchSystemIoCaseSensitivity
{
    private const string TypeName = "System.IO.PathInternal";
    private const string MethodName = "GetIsCaseSensitive";

    internal static int Run(string assemblyDirectory)
    {
        if (!Directory.Exists(assemblyDirectory))
        {
            Console.Error.WriteLine($"[system-io-patch] directory does not exist: {assemblyDirectory}");
            return 1;
        }

        var assemblies = Directory.GetFiles(assemblyDirectory, "*.dll")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var found = 0;
        var rewritten = 0;

        foreach (var path in assemblies)
        {
            var staging = path + ".system-io-patched";
            var changed = false;
            try
            {
                using (var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    ReadSymbols = false,
                    InMemory = true,
                }))
                {
                    foreach (var type in AllTypes(assembly.MainModule.Types))
                    {
                        if (type.FullName != TypeName) continue;
                        foreach (var method in type.Methods)
                        {
                            if (method.Name != MethodName || !method.IsStatic ||
                                method.Parameters.Count != 0 ||
                                method.ReturnType.FullName != "System.Boolean" || !method.HasBody)
                            {
                                continue;
                            }

                            found++;
                            if (!IsConstantFalse(method))
                            {
                                var body = method.Body;
                                body.Instructions.Clear();
                                body.ExceptionHandlers.Clear();
                                body.Variables.Clear();
                                body.InitLocals = false;
                                body.MaxStackSize = 1;
                                var il = body.GetILProcessor();
                                il.Append(il.Create(OpCodes.Ldc_I4_0));
                                il.Append(il.Create(OpCodes.Ret));
                                changed = true;
                                rewritten++;
                            }
                            Console.WriteLine(
                                $"[system-io-patch] {Path.GetFileName(path)}: " +
                                $"{method.FullName} -> false");
                        }
                    }
                    if (changed) assembly.Write(staging);
                }

                if (changed) File.Move(staging, path, overwrite: true);
            }
            catch (Exception error)
            {
                File.Delete(staging);
                Console.Error.WriteLine(
                    $"[system-io-patch] could not patch {Path.GetFileName(path)}: {error.Message}");
                return 1;
            }
        }

        if (found == 0)
        {
            Console.Error.WriteLine(
                "[system-io-patch] no System.IO.PathInternal.GetIsCaseSensitive method was found");
            return 1;
        }

        Console.WriteLine(
            $"[system-io-patch] verified {found} case-sensitivity method(s); " +
            $"rewrote {rewritten}");
        return 0;
    }

    private static bool IsConstantFalse(MethodDefinition method)
    {
        var instructions = method.Body.Instructions
            .Where(instruction => instruction.OpCode != OpCodes.Nop)
            .ToArray();
        return instructions.Length == 2 &&
            instructions[0].OpCode == OpCodes.Ldc_I4_0 &&
            instructions[1].OpCode == OpCodes.Ret &&
            method.Body.ExceptionHandlers.Count == 0;
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes)) yield return nested;
        }
    }
}
