using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpegVideoRenderer;
using SkiaSharp;
using Spectre.Console;

#region Test MediaSource
//var mediaPath = @"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4";
//var mediaStream = File.OpenRead(mediaPath);
//MediaSource mediaSource = new MediaSource(mediaStream);

//using var bitmap = new SKBitmap(mediaSource.VideoFrameWidth, mediaSource.VideoFrameHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
//var stopwatch = Stopwatch.StartNew();

//int i = 0;
//while (true)
//{
//    //var ms = stopwatch.ElapsedMilliseconds;
//    i += 30;
//    if (i < 0)
//        i = 0;

//    if ( mediaSource.GetVideoFrame(stopwatch.Elapsed) is VideoFrame frame)
//    {
//        frame.FillBitmap(bitmap);


//        var canvasImage = new CanvasImage(bitmap.Encode(SKEncodedImageFormat.Jpeg, 100).AsSpan());
//        Console.SetCursorPosition(0, 0);
//        AnsiConsole.Write(canvasImage);
//    }
//}


//Console.WriteLine("Done.");
//Console.ReadKey(true);

#endregion

#region Test Rendering
//using var video1 = File.OpenRead(@"E:\CloudMusic\MV\Erdenebat Baatar,Lkhamragchaa Lkhagvasuren,Altanjargal - Goyo (feat. Lkhamragchaa Lkhagvasuren, Altanjargal, Erdenechimeg G, Narandulam, Dashnyam & Uul Us).mp4");
//using var video2 = File.OpenRead(@"E:\CloudMusic\MV\ナナツカゼ,PIKASONIC,なこたんまる - 春めく.mp4");
//using var output = File.Create("output.mp4");

//var project = new Project()
//{
//    OutputWidth = 800,
//    OutputHeight = 600,
//    VideoResources =
//    {
//        new ProjectResource("1", video1),
//        new ProjectResource("2", video2),
//    },
//    VideoTracks =
//    {
//        new Track()
//        {
//            Children =
//            {
//                new TrackItem()
//                {
//                    ResourceId = "1",
//                    Offset = TimeSpan.FromSeconds(0),
//                    StartTime = TimeSpan.FromSeconds(0),
//                    EndTime = TimeSpan.FromSeconds(6),
//                },
//                //new TrackItem()
//                //{
//                //    ResourceId = "2",
//                //    Offset = TimeSpan.FromSeconds(4),
//                //    StartTime = TimeSpan.FromSeconds(0),
//                //    EndTime = TimeSpan.FromSeconds(10),
//                //}
//            }
//        }
//    }
//};

//Renderer.Render(project, output, null);
#endregion


using var audioToDecode =
    File.OpenRead(@"E:\CloudMusic\MV\Erdenebat Baatar,Lkhamragchaa Lkhagvasuren,Altanjargal - Goyo (feat. Lkhamragchaa Lkhagvasuren, Altanjargal, Erdenechimeg G, Narandulam, Dashnyam & Uul Us).mp4");
using var audioSourceToDecode = new MediaSource(audioToDecode);
var pcm = new List<float>();

for (int i = 0; i < 60; i++)
{
    for (int j = 0; j < 22050; j++)
    {
        var time = TimeSpan.FromSeconds(i + j / (22050.0));
        if (audioSourceToDecode.GetAudioSample(time) is AudioSample sample)
        {
            pcm.Add(sample.LeftValue);
            //pcm.Add(sample.RightValue);
        }
        else
        {
            pcm.Add(0);
            //pcm.Add(0);
        }
    }
}

//pcm = MediaSource.s_pcmLeftSamples;

var pcmArray = pcm.ToArray();
var pcmSpan = pcmArray.AsSpan();
var pcmByteSpan = MemoryMarshal.AsBytes(pcmSpan);

File.WriteAllBytes("output.pcm", pcmByteSpan.ToArray());

Console.WriteLine("OK");
