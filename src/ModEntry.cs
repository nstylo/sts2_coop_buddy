using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBuddy;

[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    public const string Id = "CoopBuddy";
    internal static readonly Logger Logger = new(Id, LogType.Generic);

    public static void Initialize()
    {
        new Harmony(Id).PatchAll(typeof(ModEntry).Assembly);
        RunManager.Instance.RoomExited += RewardTracker.Clear;
        RunManager.Instance.RunStarted += _ => RewardTracker.Clear();
        Logger.Info("Loaded Co-op Buddy 0.6.0.");
    }

    // A viewer failure must not interrupt the game's reward or networking logic.
    internal static void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) { Logger.Error(exception.ToString()); }
    }
}
