namespace FFmpegVideoRenderer
{
    public record ProjectResource(string Id, Func<Stream> StreamFactory)
    {
    }
}
