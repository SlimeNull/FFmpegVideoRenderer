namespace FFmpegVideoRenderer
{

    public abstract record class TrackItem
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

        /// <summary>
        /// Duration of this track item
        /// </summary>
        public TimeSpan Duration => EndTime - StartTime;

        /// <summary>
        /// Absolute end time in track
        /// </summary>
        public TimeSpan AbsoluteEndTime => Offset + Duration;

        public bool IsTimeInRange(TimeSpan time)
        {
            return time >= Offset && time <= AbsoluteEndTime;
        }

        public static bool GetIntersectionRate(ref TrackItem item1, ref TrackItem item2, TimeSpan time, out TimeSpan duration, out double rate)
        {
            if (item1.Offset > item2.Offset)
            {
                (item1, item2) = (item2, item1);
            }

            if (item2.Offset > item1.AbsoluteEndTime ||
                item1.AbsoluteEndTime > item2.AbsoluteEndTime)
            {
                rate = default;
                duration = default;
                return false;
            }

            TimeSpan intersectionStart = item2.Offset;
            TimeSpan intersectionDuration = item1.AbsoluteEndTime - item2.Offset;

            duration = intersectionDuration;
            rate = (time - intersectionStart) / intersectionDuration;
            return true;
        }
    }
}
