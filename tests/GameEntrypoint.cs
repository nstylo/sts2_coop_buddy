using System.Reflection;
using System.Runtime.InteropServices;
using Godot;
using Godot.Bridge;
using Godot.NativeInterop;

// Test-only Godot host glue. The production mod has no custom Godot scripts.
namespace GodotPlugins.Game;

internal static class Main
{
    [UnmanagedCallersOnly(EntryPoint = "godotsharp_game_main_init")]
    private static godot_bool InitializeFromGameProject(nint handle, nint managed, nint native, int size)
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(GodotObject).Assembly,
                new GodotDllImportResolver(handle).OnResolveDllImport);
            NativeFuncs.Initialize(native, size);
            ManagedCallbacks.Create(managed);
            ScriptManagerBridge.LookupScriptsInAssembly(typeof(MegaCrit.Sts2.Core.Nodes.NGame).Assembly);
            Callable.From(() => CoopBuddy.Tests.RuntimeTests.Run()).CallDeferred();
            return godot_bool.True;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return godot_bool.False;
        }
    }
}
