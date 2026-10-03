using SqlChangeTracking;

namespace Consumer;

/// <summary>
/// Compiles like code outside the library. A type with the same name as its namespace can't be used from there (CS0118),
/// so this fails to build if the class and the namespace get the same name.
/// </summary>
internal static class ConsumerProbe
{
    public static SqlChangeTrackingHelper Create() => new("dbo.Table");
}
