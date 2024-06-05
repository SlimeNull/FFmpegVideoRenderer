using System.Runtime.InteropServices;
using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public record struct VideoFrame(byte[] Data, int RowPitch, int BytesPerPixel, SKColorType ColorType)
    {
        public int Width => RowPitch / BytesPerPixel;
        public int Height => Data.Length / RowPitch;

        public unsafe void FillBitmap(SKBitmap bitmap)
        {
            if (bitmap.Width != Width ||
                bitmap.Height != Height ||
                bitmap.BytesPerPixel != BytesPerPixel ||
                bitmap.ColorType != ColorType)
            {
                throw new ArgumentException("Invalid bitmap, width, height or color type not match");
            }

            var height = Height;
            var rowPitch = RowPitch;

            var pBitmapData = (byte*)bitmap.GetPixels();
            fixed (byte* pFrameData = Data)
            {
                for (int i = 0; i < height; i++)
                {
                    var offset = rowPitch * i;
                    NativeMemory.Copy(pFrameData + offset, pBitmapData + offset, (nuint)rowPitch);
                }
            }
        }
    }
}
