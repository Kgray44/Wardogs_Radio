namespace WardogsRadio.Voicemeeter;

public static class VoicemeeterDeviceRestorer
{
    public static bool TryClearA1(IVoicemeeterRemote mixer)
    {
        foreach (var driver in new[] { "mme", "wdm", "ks" })
        {
            if (!mixer.TrySetParameterString("Bus[0].device." + driver, "")) continue;
            if (mixer.TryGetParameterString("Bus[0].device.name", out var name) && string.IsNullOrWhiteSpace(name))
                return true;
        }
        return false;
    }
}
