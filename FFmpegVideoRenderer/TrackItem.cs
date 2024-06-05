namespace FFmpegVideoRenderer
{

    public abstract class TrackItem
    {
        public string ResourceId { get; set; } = "SomeResource";

        /// <summary>
        /// The time offset of this track item
        /// </summary>
        public TimeSpan Offset { get; set; }

        /// <summary>
        /// Relative start time to resource
        /// </summary>
        public TimeSpan StartTime { get; set; }

        /// <summary>
        /// Relative end time to resource
        /// </summary>
        public TimeSpan EndTime { get; set; }
    }
}
