namespace GtaForge.Engine.Peds;

/// <summary>The 12 ped variation components, in ePedVarComp order.</summary>
public static class PedComponents
{
    public sealed record Comp(int Index, string Short, string Enum);

    public static readonly Comp[] All =
    [
        new(0, "head", "PV_COMP_HEAD"),
        new(1, "berd", "PV_COMP_BERD"),
        new(2, "hair", "PV_COMP_HAIR"),
        new(3, "uppr", "PV_COMP_UPPR"),
        new(4, "lowr", "PV_COMP_LOWR"),
        new(5, "hand", "PV_COMP_HAND"),
        new(6, "feet", "PV_COMP_FEET"),
        new(7, "teef", "PV_COMP_TEEF"),
        new(8, "accs", "PV_COMP_ACCS"),
        new(9, "task", "PV_COMP_TASK"),
        new(10, "decl", "PV_COMP_DECL"),
        new(11, "jbib", "PV_COMP_JBIB"),
    ];

    public static Comp ByShort(string s) => All.First(c => c.Short == s);

    /// <summary>Drawable name in a ped .ydd, e.g. uppr_000_u.</summary>
    public static string DrawableName(Comp c, int index, char race = 'u') => $"{c.Short}_{index:000}_{race}";

    /// <summary>Diffuse texture name the variation system looks up, e.g. uppr_diff_000_a_uni.</summary>
    public static string DiffuseName(Comp c, int index, char letter = 'a', string race = "uni") =>
        $"{c.Short}_diff_{index:000}_{letter}_{race}";

    public static string NormalName(Comp c, int index) => $"{c.Short}_normal_{index:000}";
    public static string SpecName(Comp c, int index) => $"{c.Short}_spec_{index:000}";

    /// <summary>
    /// Which body component a bone's skin belongs to when a single full-body mesh is split.
    /// Neck and face bones go to the head, pelvis/legs to lowr, feet/toes to feet, the rest to uppr.
    /// </summary>
    public static string ForBone(string boneName)
    {
        var n = boneName.ToLowerInvariant();
        if (n.StartsWith("fb_") || n.StartsWith("facial_") || n.Contains("head") || n.Contains("neck")
            || n.Contains("jaw") || n.Contains("eye") || n.Contains("brow") || n.Contains("lip") || n.Contains("hair"))
            return "head";
        if (n.Contains("foot") || n.Contains("toe") || n.Contains("ankle") || n.Contains("heel"))
            return "feet";
        if (n.Contains("pelvis") || n.Contains("thigh") || n.Contains("calf") || n.Contains("knee")
            || n.Contains("skirt") || n.Contains("spine_root") || n == "skel_root" || n.Contains("hip") || n.Contains("butt"))
            return "lowr";
        return "uppr";
    }
}
