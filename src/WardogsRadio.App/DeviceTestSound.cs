using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

internal static class DeviceTestSound
{
    public static async Task PlayThroughMpvAsync(string endpointId, string playerDeviceId, string? mpvPath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), "wardogs-listening-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            const int sampleRate = 48_000;
            const int frames = sampleRate * 2;
            var format = new WaveFormat(sampleRate, 16, 2);
            var samples = new byte[frames * format.BlockAlign];
            for (var frame = 0; frame < frames; frame++)
            {
                var fade = Math.Min(1d, Math.Min(frame, frames - 1 - frame) / (sampleRate * .035));
                var sample = (short)(Math.Sin(2 * Math.PI * 523.25 * frame / sampleRate) * 32767 * .15 * fade);
                var offset = frame * format.BlockAlign;
                samples[offset] = samples[offset + 2] = (byte)sample;
                samples[offset + 1] = samples[offset + 3] = (byte)(sample >> 8);
            }
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new WaveFileWriter(file, format)) writer.Write(samples, 0, samples.Length);
            await using var player = new MpvProvider(new MpvLocator(), mpvPath, playerDeviceId);
            await player.LoadAsync(new Station { Name = "Listening output test", Source = path }, cancellationToken);
            await player.SetVolumeAsync(.25, cancellationToken);
            await player.PlayAsync(cancellationToken);
            using var meter = new WindowsAudioPeakMeter();
            var observed = false;
            var outputOpen = false;
            for (var attempt = 0; attempt < 18; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(await player.ReadAudioDeviceAsync(cancellationToken), playerDeviceId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The player did not retain the selected listening output.");
                var ao = await player.ReadCurrentAudioOutputAsync(cancellationToken);
                outputOpen |= ao?.StartsWith("wasapi", StringComparison.OrdinalIgnoreCase) == true;
                if (meter.TryRead(endpointId, out var peak) && peak > .005f) observed = true;
                if (outputOpen && observed) break;
                await Task.Delay(75, cancellationToken);
            }
            if (!outputOpen) throw new InvalidOperationException("The player did not open the selected Windows audio output.");
            if (!observed) throw new InvalidOperationException("The player opened the output, but no test signal was observed at the selected endpoint.");
            await player.StopAsync(cancellationToken);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    public static async Task PlayAsync(string endpointId, CancellationToken cancellationToken = default)
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
        await Task.Delay(650, cancellationToken);
        player.Stop();
    }
}
