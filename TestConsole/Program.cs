using FFmpegVideoRenderer;
using SkiaSharp;

var mediaPath = @"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4";
var mediaStream = File.OpenRead(mediaPath);
MediaSource mediaSource = new MediaSource(mediaStream);

using var bitmap = new SKBitmap(mediaSource.VideoFrameWidth, mediaSource.VideoFrameHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);

for (int i = 0; i < 30 && i < mediaSource.VideoFrameCount; i++)
{
    var frame = mediaSource.GetVideoFrame(i * 30 * 10 * 5);


    frame.FillBitmap(bitmap);
    using var output = File.Create($"output{i}.png");
    bitmap.Encode(output, SKEncodedImageFormat.Png, 114514);
}

Console.WriteLine("Done.");
Console.ReadKey(true);
