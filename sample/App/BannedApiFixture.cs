namespace SampleApp;

// Fixture for analyzer-driven diagnostics: calls a banned API (System.GC.Collect)
// so the BannedApiAnalyzers rule RS0030 fires here and nowhere else in the sample.
public static class BannedApiFixture
{
    public static void ForceCollect()
    {
        GC.Collect();
    }
}
