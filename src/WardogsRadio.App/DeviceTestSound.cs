using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WardogsRadio.App;

internal static class DeviceTestSound
{
    public static async Task PlayAsync(string endpointId)
    {
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDevice(endpointId.Split('\\').Last());
        if (device.DataFlow != DataFlow.Render)
            throw new InvalidOperationException("The selected device is not a listening output.");

        const int sampleRate = 48_000;
        const int frames = sampleRate / 2;
        var format = new WaveFormat(sampleRate, 16, 2);
        var samples = new byte[frames * format.BlockAlign];
        for (var frame = 0; frame < frames; frame++)
        {
            var fade = Math.Min(1d, Math.Min(frame, frames - 1 - frame) / (sampleRate * .025));
            var sample = (short)(Math.Sin(2 * Math.PI * 523.25 * frame / sampleRate) * 32767 * .12 * fade);
            var offset = frame * format.BlockAlign;
            samples[offset] = (byte)sample;
            samples[offset + 1] = (byte)(sample >> 8);
            samples[offset + 2] = (byte)sample;
            samples[offset + 3] = (byte)(sample >> 8);
        }
        var buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(1)) { ReadFully = true };
        buffer.AddSamples(samples, 0, samples.Length);
        using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
        player.Init(buffer);
        player.Play();
        await Task.Delay(650);
        player.Stop();
    }
}
