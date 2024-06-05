namespace FFmpegVideoRenderer
{
    public abstract class Track
    {
        public List<TrackItem> Children { get; } = new();
    }
}
