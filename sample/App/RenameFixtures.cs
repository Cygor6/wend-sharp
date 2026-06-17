// Test fixtures for the upgraded PlanRename: override cascade, nameof/cref classification,
// and a conflict target. These do NOT reference Calculator.Add, so existing FindReferences
// counts are unaffected.
namespace SampleApp;

/// <summary>Rename fixture: a virtual method with an override in a derived class.</summary>
public class WidgetBase
{
    /// <summary>See <see cref="DoWork"/> for what this member does.</summary>
    public virtual void DoWork() { }
}

public class WidgetDerived : WidgetBase
{
    public override void DoWork() { }   // cascade target: Reason == "Override"
}

public static class WidgetNames
{
    // Renaming DoWork must update this nameof(...) argument (Location == "Nameof").
    public static readonly string Token = nameof(WidgetBase.DoWork);
}

/// <summary>
/// Rename fixture: renaming Alpha to Beta collides with the existing Beta — produces a
/// RenameConflict (CS0111 duplicate signature).
/// </summary>
public class ConflictTarget
{
    public void Alpha() { }
    public void Beta() { }
}
