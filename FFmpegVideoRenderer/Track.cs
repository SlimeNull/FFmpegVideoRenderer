namespace FFmpegVideoRenderer
{
    public class Track<TTrackItem> where TTrackItem : TrackItem
    {
        public List<TTrackItem> Children { get; } = new();
    }
}
