using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 1)
    throw new ArgumentException("Usage: PortabilityChecks /path/to/CoopBuddy.dll");

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var clr = pe.PEHeaders.CorHeader ?? throw new InvalidDataException("Not a managed .NET assembly");
var metadata = pe.GetMetadataReader();
Check(pe.PEHeaders.CoffHeader.Machine == Machine.I386 && (clr.Flags & CorFlags.ILOnly) != 0 &&
    (clr.Flags & (CorFlags.Requires32Bit | CorFlags.Prefers32Bit | CorFlags.NativeEntryPoint)) == 0,
    "AnyCPU managed IL, with no native or 32-bit entrypoint");
Check(metadata.MethodDefinitions.All(handle =>
    (metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.PinvokeImpl) == 0),
    "no platform-specific P/Invoke methods in the production DLL");
var references = metadata.AssemblyReferences.Select(handle =>
    metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToHashSet();
Check(new[] { "sts2", "GodotSharp", "0Harmony" }.All(references.Contains),
    "game-provided runtime dependencies are referenced");
Check(references.All(name => name is "sts2" or "GodotSharp" or "0Harmony" ||
    name.StartsWith("System.", StringComparison.Ordinal)),
    "no additional runtime libraries to deploy");
Check(!references.Any(name => name is "System.Windows.Forms" or "System.Drawing.Common" ||
    name.StartsWith("System.Windows.", StringComparison.Ordinal)),
    "no Windows desktop framework references");
Console.WriteLine("COOP_BUDDY_PORTABILITY_PASSED: 5");

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("PASS: " + name);
}
