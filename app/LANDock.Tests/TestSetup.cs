using System.Runtime.CompilerServices;
using LANDock.Core;

namespace LANDock.Tests;

internal static class TestSetup
{
    /// <summary>Tests never touch the real data folder (logs, settings).</summary>
    [ModuleInitializer]
    internal static void Init() => Paths.DataDir = Path.Combine(Path.GetTempPath(), "landock-tests-data");
}
