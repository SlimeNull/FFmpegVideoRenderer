using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public class SlideXTransition : IVideoTransition
    {
        public void Render(SKCanvas canvas, SKSize canvasSize, SKBitmap fromFrame, SKRect fromFrameDest, SKBitmap toFrame, SKRect toFrameDest, TimeSpan transitionDuration, float rate)
        {
            fromFrameDest.Location -= new SKPoint(canvasSize.Width * rate, 0);
            canvas.DrawBitmap(fromFrame, fromFrameDest);

            toFrameDest.Location += new SKPoint(canvasSize.Width * (1 - rate), 0);
            canvas.DrawBitmap(toFrame, toFrameDest);
        }
    }
}
