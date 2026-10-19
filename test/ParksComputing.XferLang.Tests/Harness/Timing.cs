using System;
using System.Globalization;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// The time budgets of the tests marked <c>Category=Timing</c>, which check that work is linear
/// and not quadratic, scaled by <c>XFER_TIMING_SCALE</c> (1 when unset). Coverage instrumentation
/// slows the code several times over, so the coverage run sets a larger scale rather than leaving
/// those tests, and the code only they reach, out of it.
/// </summary>
internal static class Timing
{
    public static int Scale { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("XFER_TIMING_SCALE"), NumberStyles.None, CultureInfo.InvariantCulture, out int scale) && scale > 0 ? scale : 1;
}
