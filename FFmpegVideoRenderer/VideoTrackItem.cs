namespace FFmpegVideoRenderer
{
    public record class VideoTrackItem : TrackItem
    {
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public int SizeWidth { get; set; }
        public int SizeHeight { get; set; }

        public bool MuteAudio { get; set; }

        public VideoTransition Transition { get; set; }
    }
}
