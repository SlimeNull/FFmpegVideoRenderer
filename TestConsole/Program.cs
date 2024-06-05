using System.Diagnostics;
using FFmpegVideoRenderer;
using SkiaSharp;
using Spectre.Console;

var mediaPath = @"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4";
var mediaStream = File.OpenRead(mediaPath);
MediaSource mediaSource = new MediaSource(mediaStream);

using var bitmap = new SKBitmap(mediaSource.VideoFrameWidth, mediaSource.VideoFrameHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
//var stopwatch = Stopwatch.StartNew();

int i = 0;
while (true)
{
    //var ms = stopwatch.ElapsedMilliseconds;
    i += 30;
    if (i < 0)
        i = 0;

    var frame = mediaSource.GetVideoFrame(i);

    //frame.FillBitmap(bitmap);


    //var canvasImage = new CanvasImage(bitmap.Encode(SKEncodedImageFormat.Jpeg, 100).AsSpan());
    //Console.SetCursorPosition(0, 0);
    //AnsiConsole.Write(canvasImage);
}


Console.WriteLine("Done.");
Console.ReadKey(true);
