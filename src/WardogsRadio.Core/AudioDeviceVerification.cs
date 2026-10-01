namespace WardogsRadio.Core;

public static class AudioDeviceVerification
{
    public static SetupVerification? InvalidateMicrophone(SetupVerification? saved) =>
        saved is null ? null : saved with { MicrophoneDeviceId = null, MicrophoneObserved = false };
    public static SetupVerification? InvalidateListening(SetupVerification? saved) =>
        saved is null ? null : saved with { MonitorDeviceId = null, ListeningConfirmed = false };
}
