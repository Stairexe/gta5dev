namespace GtaForge.Engine.Peds;

public sealed class PedRequest
{
    /// <summary>Model name (spawn name), e.g. "my_ped".</summary>
    public string Name { get; set; } = "my_addon_ped";
    /// <summary>CIVMALE | CIVFEMALE | COP | MEDIC | ... (peds.meta Pedtype).</summary>
    public string PedType { get; set; } = "CIVMALE";
    /// <summary>auto = split a single full-body drawable into head/uppr/lowr/feet; keep = use drawables as named.</summary>
    public string SplitMode { get; set; } = "auto";
    public bool TestCommand { get; set; } = true;
}

public sealed record PedInput(byte[] Ydd, byte[]? Ytd, byte[]? Yft);
