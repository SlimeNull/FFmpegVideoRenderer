using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public class FadeTransition : IVideoTransition
    {
        public void Render(SKCanvas canvas, SKSize canvasSize, SKBitmap fromFrame, SKRect fromFrameDest, SKBitmap toFrame, SKRect toFrameDest, TimeSpan transitionDuration, float rate)
        {
            using SKPaint paint = new();

            paint.Color = new SKColor(0, 0, 0, (byte)(255 * (1 - rate)));
            canvas.DrawBitmap(fromFrame, fromFrameDest, paint);

            paint.Color = new SKColor(0, 0, 0, (byte)(255 * rate));
            canvas.DrawBitmap(toFrame, toFrameDest, paint);
        }
    }
}
