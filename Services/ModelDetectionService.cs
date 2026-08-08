namespace Dispeller.Services;

public class ModelDetectionService
{
    /// <summary>
    /// Extract model information from Item.ModelMain.
    /// Based on Glamaholic's AlternativeFinder.ModelInfo.
    /// </summary>
    /// <param name="raw">The packed Item.ModelMain value.</param>
    /// <param name="ignoreVariant">
    /// Drop the trailing variant, so a recolour compares equal to the original. There is no
    /// default: which of the two a comparison wants is a policy question, and the answer is
    /// now a user setting rather than something to assume here.
    /// </param>
    public static (ushort, ushort, ushort, ushort) ExtractModelInfo(ulong raw, bool ignoreVariant)
    {
        var primaryKey = (ushort)(raw & 0xFFFF);
        var secondaryKey = (ushort)((raw >> 16) & 0xFFFF);
        var weaponVariant = (ushort)((raw >> 32) & 0xFFFF);

        // Gear packs ModelMain as modelId | variant << 16 - so for gear, secondaryKey IS the
        // variant. Weapons use three fields: primary | secondary << 16 | variant << 32, so
        // their mesh is the first two. A non-zero third field is what distinguishes a weapon
        // from a piece of gear; there is no slot information in the raw value.
        var isWeapon = weaponVariant != 0;

        // In both cases the trailing variant only swaps materials and colours - the mesh is
        // the leading field(s). Matching on the mesh alone is what makes a recolour count as
        // a redundant glamour. Weapons previously compared all four fields, which made a
        // match all but impossible: 101 distinct models across 101 main hands.
        if (ignoreVariant)
        {
            if (isWeapon)
                return (primaryKey, secondaryKey, 0, 0);

            return (primaryKey, 0, 0, 0);
        }

        if (isWeapon)
            return (primaryKey, secondaryKey, weaponVariant, 0);

        return (primaryKey, secondaryKey, 0, 0);
    }

    /// <summary>
    /// Get model ID string for display
    /// </summary>
    public static string GetModelIdString((ushort, ushort, ushort, ushort) model)
    {
        return $"{model.Item1}-{model.Item2}-{model.Item3}-{model.Item4}";
    }
}
