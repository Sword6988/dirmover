using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;

namespace DirMover.Core.Tests;

public sealed class TempProbeTests
{
    private readonly ITestOutputHelper _output;
    public TempProbeTests(ITestOutputHelper output) { _output = output; }

    [Fact]
    public void Probe()
    {
        _output.WriteLine("PROBE-COLLECTIONS=" + (TestEnvMarker.Collections ?? "(none)"));
    }
}

internal static class TestEnvMarker
{
    public static readonly string? Collections;
    static TestEnvMarker()
    {
        var t = typeof(TestEnvMarker).Assembly.GetType("DirMover.Core.Tests.TestEnv");
        Collections = t == null ? "TestEnv type NOT FOUND" : "found";
    }
}
