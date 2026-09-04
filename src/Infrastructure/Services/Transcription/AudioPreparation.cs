using System.Diagnostics;

namespace PKS.Infrastructure.Services.Transcription;

/// <summary>
/// WAV framing and part planning for 16 kHz mono s16le PCM, plus the one ffmpeg call that
/// turns an arbitrary recording into it.
///
/// The point of working in raw PCM is that everything below is arithmetic: a header is 44
/// constant-ish bytes, a "slice" is a byte range, and concatenating two takes is a copy. The
/// speech models want exactly this format, so nothing downstream ever transcodes.
///
/// ffmpeg is needed only at the front door. A recording that arrives as WAV already — which
/// is what a recorder app produces — skips it entirely, so a machine without ffmpeg can still
/// transcribe.
/// </summary>
public static class AudioPreparation
{
    public const int SampleRate = 16000;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * BytesPerSample; // 32 kB/s ≈ 115 MB/h

    public const int WavHeaderBytes = 44;

    /// <summary>Below this there is nothing worth sending to a speech model.</summary>
    public const int MinTranscribableBytes = 32_000; // 1 second

    /// <summary>
    /// Azure fast transcription takes a whole file in one request, but not an unbounded one,
    /// and a single failure on a two-hour upload costs two hours.
    /// </summary>
    public const int DefaultPartSeconds = 15 * 60;

    public static byte[] WavHeader(int pcmBytes, int sampleRate = SampleRate, short channels = 1)
    {
        var header = new byte[WavHeaderBytes];
        var byteRate = sampleRate * channels * BytesPerSample;

        void Ascii(string s, int at) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(header, at);
        void U32(uint v, int at) => BitConverter.GetBytes(v).CopyTo(header, at);
        void U16(ushort v, int at) => BitConverter.GetBytes(v).CopyTo(header, at);

        Ascii("RIFF", 0);
        U32((uint)(36 + pcmBytes), 4);
        Ascii("WAVE", 8);
        Ascii("fmt ", 12);
        U32(16, 16);                                   // PCM fmt chunk size
        U16(1, 20);                                    // format = PCM
        U16((ushort)channels, 22);
        U32((uint)sampleRate, 24);
        U32((uint)byteRate, 28);
        U16((ushort)(channels * BytesPerSample), 32);  // block align
        U16(8 * BytesPerSample, 34);                   // bits per sample
        Ascii("data", 36);
        U32((uint)pcmBytes, 40);

        return header;
    }

    public static byte[] PcmToWav(ReadOnlySpan<byte> pcm, int sampleRate = SampleRate)
    {
        var wav = new byte[WavHeaderBytes + pcm.Length];
        WavHeader(pcm.Length, sampleRate).CopyTo(wav, 0);
        pcm.CopyTo(wav.AsSpan(WavHeaderBytes));

        return wav;
    }

    public static long PcmDurationMs(long bytes) => (long)Math.Round((double)bytes / BytesPerSecond * 1000);

    /// <summary>One transcribable slice of the recording. Offsets are in the whole-file timeline.</summary>
    public readonly record struct Part(int Index, long Start, long End, long OffsetMs)
    {
        public long Length => End - Start;
    }

    /// <summary>
    /// Cut a long recording into transcribable parts. Cutting mid-word is accepted: at a
    /// 15-minute boundary it costs at most one word, and the alternative (silence detection)
    /// is a signal-processing problem we do not need to have.
    /// </summary>
    public static List<Part> PlanParts(long totalBytes, int partSeconds = DefaultPartSeconds)
    {
        var partBytes = AlignToSample((long)partSeconds * BytesPerSecond);
        var parts = new List<Part>();
        var index = 0;
        for (long start = 0; start < totalBytes; start += partBytes)
        {
            var end = Math.Min(totalBytes, start + partBytes);
            parts.Add(new Part(index++, start, end, PcmDurationMs(start)));
        }

        return parts.Count > 0 ? parts : [new Part(0, 0, 0, 0)];
    }

    private static long AlignToSample(long bytes) => bytes - bytes % BytesPerSample;

    /// <summary>
    /// Decode any container to 16 kHz mono s16le PCM. Returns the path of the WAV written to
    /// <paramref name="destinationWav"/>.
    /// </summary>
    public static async Task<string> ExtractWavAsync(
        string mediaPath, string destinationWav, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", mediaPath,
            "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-c:a", "pcm_s16le",
            destinationWav,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("ffmpeg not found. Install it, or pass a 16 kHz mono WAV directly.");
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed ({process.ExitCode}): {stderr.Trim()}");
        }

        return destinationWav;
    }

    /// <summary>
    /// Read the PCM payload out of a WAV file, skipping to the data chunk. Tolerates the
    /// extra chunks (LIST, fact) that some encoders write before it.
    /// </summary>
    public static byte[] ReadPcm(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        if (bytes.Length < 12 ||
            System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidOperationException($"{wavPath} is not a RIFF/WAVE file.");
        }

        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToUInt32(bytes, at + 4);
            var payload = at + 8;
            if (id == "data")
            {
                var length = (int)Math.Min(size, (uint)(bytes.Length - payload));

                return bytes.AsSpan(payload, length).ToArray();
            }
            at = payload + (int)size + ((int)size % 2); // chunks are word-aligned
        }

        throw new InvalidOperationException($"{wavPath} has no data chunk.");
    }
}
