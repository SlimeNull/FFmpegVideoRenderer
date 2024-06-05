using System.Runtime.InteropServices;
using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public record struct VideoFrame(int Width, int Height, byte[] Data, int RowPitch, int BytesPerPixel, SKColorType ColorType)
    {
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
            var rowBytes = BytesPerPixel * Width;
            var frameRowPitch = RowPitch;
            var bitmapRowPitch = bitmap.RowBytes;

            var pBitmapData = (byte*)bitmap.GetPixels();
            fixed (byte* pFrameData = Data)
            {
                for (int i = 0; i < height; i++)
                {
                    var frameDataOffset = frameRowPitch * i;
                    var bitmapDataOffset = bitmapRowPitch * i;
                    NativeMemory.Copy(pFrameData + frameDataOffset, pBitmapData + bitmapDataOffset, (nuint)rowBytes);
                }
            }
        }
    }
}
