namespace FFmpegVideoRenderer
{
    public record struct AudioFrame(AudioSample[] Samples, int SampleCount);
}
