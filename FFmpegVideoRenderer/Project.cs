namespace FFmpegVideoRenderer
{
    public class Project
    {
        public string? Name { get; set; }

        public List<ProjectResource> Resources { get; } = new();

        public List<AudioTrack> AudioTracks { get; } = new();
        public List<VideoTrack> VideoTracks { get; } = new();

        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
    }
}
