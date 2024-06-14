using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public interface IVideoTransition
    {
        public void Render(SKCanvas canvas, SKSize canvasSize, SKBitmap fromFrame, SKRect fromFrameDest, SKBitmap toFrame, SKRect toFrameDest, TimeSpan transitionDuration, float rate);
    }
}
