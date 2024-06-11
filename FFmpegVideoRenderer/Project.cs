namespace FFmpegVideoRenderer
{
    public class Project
    {
        public List<ProjectResource> AudioResources { get; } = new();
        public List<ProjectResource> VideoResources { get; } = new();

        public List<Track> AudioTracks { get; } = new();
        public List<Track> VideoTracks { get; } = new();

        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
    }
}
