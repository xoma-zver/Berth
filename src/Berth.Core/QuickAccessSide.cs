namespace Berth;

/// <summary>Stripe hosting the quick access «⋯» button (spec TW-8.1, TW-5.15). Only Left and Right are valid.</summary>
public enum QuickAccessSide
{
    /// <summary>End of the Left.Secondary stripe segment.</summary>
    Left,

    /// <summary>End of the Right.Secondary stripe segment.</summary>
    Right,
}
