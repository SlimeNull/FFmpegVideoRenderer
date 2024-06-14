# FFmpegVideoRenderer

基于 FFmpeg 的视频剪辑渲染器. 支持多视频与音频轨道, 音频与视频的过渡. 在媒体上, 理论支持 ffmpeg 能够解码与编码的所有音频视频以及图片格式.

<br />

## 使用

- 克隆并引用此项目
- 使用, 调用提供的功能即可

<br />

## 功能

本库主要提供了两个功能, 一是高度封装的视频剪辑, 二是它下面的 MediaSource 封装, 用于从媒体文件中获取帧与采样.

### 视频剪辑

只需要提供一个 "配置信息", 然后把它传给渲染方法, 就可以完成整个渲染过程了! 下面是一个简单的拼合两个视频前几秒的示例:

```csharp
// 打开两个素材
using var video1 = File.OpenRead(@"E:\CloudMusic\MV\Goyo.mp4");
using var video2 = File.OpenRead(@"E:\CloudMusic\MV\春めく.mp4");

// 保存位置
using var output = File.Create("output.mp4");

// 声明项目
var project = new Project()
{
    OutputWidth = 800,
    OutputHeight = 600,
    Resources =
    {
        new ProjectResource("1", video1),
        new ProjectResource("2", video2),
    },
    VideoTracks =
    {
        new VideoTrack()
        {
            Children =
            {
                new VideoTrackItem()
                {
                    ResourceId = "1",
                    Offset = TimeSpan.FromSeconds(0),
                    StartTime = TimeSpan.FromSeconds(0),
                    EndTime = TimeSpan.FromSeconds(6),
                },
                new VideoTrackItem()
                {
                    ResourceId = "2",
                    Offset = TimeSpan.FromSeconds(4),
                    StartTime = TimeSpan.FromSeconds(0),
                    EndTime = TimeSpan.FromSeconds(30),
                }
            }
        },
    },
};

// 渲染
VideoRenderer.Render(project, output);
```

### 视频源

使用 MediaSource 类, 你可以轻松从媒体文件中获取帧数据或者采样.

```csharp
using var video1 = File.OpenRead(@"E:\CloudMusic\MV\Goyo.mp4");

```
