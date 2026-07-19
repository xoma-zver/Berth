namespace Berth;

/// <summary>
/// Domain guard for enum values entering the core through programmatic inputs — commands and
/// the state/descriptor constructors. An out-of-domain value (cast past the enum's defined
/// members) is a caller error thrown as <see cref="ArgumentOutOfRangeException"/>: without the
/// guard the value would survive to serialization and break
/// <see cref="LayoutPersistence.Serialize"/> on a state produced by a regular operation. The
/// file path is domain-checked by <see cref="LayoutPersistence.Deserialize"/>; a value injected
/// into the immutable model through <c>with</c>, bypassing these entry points, remains the
/// caller's responsibility.
/// </summary>
internal static class EnumDomain
{
    /// <summary>Guards one enum value against its defined domain.</summary>
    /// <param name="value">The value to validate.</param>
    /// <param name="paramName">Name of the offending parameter, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is outside its enum domain.</exception>
    public static void Require<TEnum>(TEnum value, string paramName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{typeof(TEnum).Name} value is outside its domain.");
        }
    }

    /// <summary>Guards both members of a slot against their domains.</summary>
    /// <param name="slot">The slot to validate.</param>
    /// <param name="paramName">Name of the offending parameter, for the exception.</param>
    /// <exception cref="ArgumentOutOfRangeException">A member of <paramref name="slot"/> is outside its enum domain.</exception>
    public static void Require(ToolWindowSlot slot, string paramName)
    {
        Require(slot.Side, paramName);
        Require(slot.Group, paramName);
    }
}
