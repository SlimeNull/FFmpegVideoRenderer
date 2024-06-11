using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;
using SkiaSharp;

namespace FFmpegVideoRenderer
{
    public static class Renderer
    {
        static readonly AVRational Framerate = new AVRational(1, 30);

        static bool HasMoreVideoFrames(Project project, TimeSpan time)
        {
            foreach (var track in project.VideoTracks)
            {
                if (track.Children.Any(item => item.AbsoluteEndTime > time))
                    return true;
            }

            return false;
        }

        static TimeSpan GetMediaSourceFrameTime(TrackItem trackItem, TimeSpan globalTime)
        {
            return globalTime - trackItem.Offset + trackItem.StartTime;
        }

        static SKRect LayoutVideoTrackItem(Project project, TrackItem videoTrackItem)
        {
            return new SKRect(0, 0, project.OutputWidth, project.OutputHeight);
        }

        public static unsafe void Render(Project project, Stream outputStream, IProgress<RenderProgress>? progress)
        {
            Dictionary<string, MediaSource> _audioMediaSources = new();
            Dictionary<string, MediaSource> _videoMediaSources = new();

            // prepare resources
            foreach (var audioResource in project.AudioResources)
            {
                _audioMediaSources[audioResource.Id] = new MediaSource(audioResource.SourceStream);
            }

            foreach (var videoResource in project.VideoResources)
            {
                _videoMediaSources[videoResource.Id] = new MediaSource(videoResource.SourceStream);
            }


            // prepare rendering
            using FormatContext formatContext = FormatContext.AllocOutput(formatName: "mp4");
            formatContext.VideoCodec = Codec.CommonEncoders.Libx264;
            formatContext.AudioCodec = Codec.CommonEncoders.Libmp3lame;

            using CodecContext videoEncoder = new CodecContext(formatContext.VideoCodec)
            {
                Width = project.OutputWidth,
                Height = project.OutputHeight,
                Framerate = Framerate,
                TimeBase = Framerate,
                PixelFormat = AVPixelFormat.Yuv420p,
                Flags = AV_CODEC_FLAG.GlobalHeader,
            };

            AVChannelLayout avChannelLayout = default;
            ffmpeg.av_channel_layout_default(&avChannelLayout, 1);

            using CodecContext audioEncoder = new CodecContext(formatContext.AudioCodec)
            {
                BitRate = 320000,
                SampleFormat = AVSampleFormat.Flt,
                SampleRate = 44100,
                ChLayout = avChannelLayout,
                CodecType = AVMediaType.Audio,
                FrameSize = 1024,
                TimeBase = new AVRational(1, 44100)
            };

            MediaStream videoStream = formatContext.NewStream(formatContext.VideoCodec);
            MediaStream audioStream = formatContext.NewStream(formatContext.AudioCodec);

            videoEncoder.Open(formatContext.VideoCodec);
            audioEncoder.Open(formatContext.AudioCodec);


            videoStream.Codecpar!.CopyFrom(videoEncoder);
            videoStream.TimeBase = videoEncoder.TimeBase;

            audioStream.Codecpar!.CopyFrom(audioEncoder);
            audioStream.TimeBase = audioEncoder.TimeBase;

            using IOContext ioc = IOContext.WriteStream(outputStream);
            formatContext.Pb = ioc;

            SKBitmap bitmap = new SKBitmap(project.OutputWidth, project.OutputHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
            SKCanvas canvas = new SKCanvas(bitmap);
            SKPaint paint = new SKPaint();

            VideoFrameConverter frameConverter = new VideoFrameConverter();


            // write header
            formatContext.WriteHeader();

            // prepare
            using var packetRef = new Packet();
            List<TrackItem> trackItemsToRender = new();

            // video encoding
            #region Video Encoding
            long frameIndex = 0;
            while (true)
            {
                var time = TimeSpan.FromSeconds((double)frameIndex * Framerate.Num / Framerate.Den);
                if (!HasMoreVideoFrames(project, time))
                {
                    break;
                }

                canvas.Clear();

                // 从下网上绘制
                foreach (var track in project.VideoTracks.Reverse<Track>())
                {
                    trackItemsToRender.Clear();
                    foreach (var trackItem in track.Children.Where(trackItem => trackItem.IsTimeInRange(time)))
                    {
                        trackItemsToRender.Add(trackItem);
                    }

                    if (trackItemsToRender.Count == 1)
                    {
                        var trackItem = trackItemsToRender[0];
                        if (_videoMediaSources.TryGetValue(trackItem.ResourceId, out var mediaSource))
                        {
                            var frameTime = (long)GetMediaSourceFrameTime(trackItem, time).TotalMilliseconds;
                            if (mediaSource.GetVideoFrameBitmap(frameTime) is SKBitmap frameBitmap)
                            {
                                var dest = LayoutVideoTrackItem(project, trackItem);
                                canvas.DrawBitmap(frameBitmap, dest);
                            }
                        }
                    }
                    else if (trackItemsToRender.Count >= 2)
                    {
                        var trackItem1 = trackItemsToRender[0];
                        var trackItem2 = trackItemsToRender[1];

                        if (_videoMediaSources.TryGetValue(trackItem1.ResourceId, out var mediaSource1) &&
                            _videoMediaSources.TryGetValue(trackItem2.ResourceId, out var mediaSource2) &&
                            TrackItem.GetIntersectionRate(ref trackItem1, ref trackItem2, time, out var rate))
                        {
                            var relativeTime1 = (long)GetMediaSourceFrameTime(trackItem1, time).TotalMilliseconds;
                            var relativeTime2 = (long)GetMediaSourceFrameTime(trackItem2, time).TotalMilliseconds;

                            if (mediaSource1.GetVideoFrameBitmap(relativeTime1) is SKBitmap frameBitmap1 &&
                                mediaSource2.GetVideoFrameBitmap(relativeTime2) is SKBitmap frameBitmap2)
                            {
                                var dest1 = LayoutVideoTrackItem(project, trackItem1);
                                var dest2 = LayoutVideoTrackItem(project, trackItem2);

                                using var paint1 = new SKPaint()
                                {
                                    Color = new SKColor(255, 255, 255, (byte)(255 - rate * 255)),
                                };

                                using var paint2 = new SKPaint()
                                {
                                    Color = new SKColor(255, 255, 255, (byte)(rate * 255)),
                                };

                                canvas.DrawBitmap(frameBitmap1, dest1, paint1);
                                canvas.DrawBitmap(frameBitmap2, dest2, paint2);
                            }

                            for (int j = 2; j < trackItemsToRender.Count; j++)
                            {
                                var trackItemOther = trackItemsToRender[j];
                                var relativeTimeOther = (long)((time - trackItemOther.Offset).TotalMilliseconds);

                                if (_videoMediaSources.TryGetValue(trackItemOther.ResourceId, out var mediaSourceOther) &&
                                    mediaSourceOther.GetVideoFrameBitmap(relativeTimeOther) is SKBitmap frameBitmapOther)
                                {

                                }
                            }
                        }
                        else
                        {

                        }
                    }
                }


                using var frame = new Frame();
                frame.Width = project.OutputWidth;
                frame.Height = project.OutputHeight;
                frame.Format = (int)AVPixelFormat.Bgra;
                frame.Data[0] = bitmap.GetPixels();
                frame.Linesize[0] = bitmap.RowBytes;
                frame.Pts = frameIndex;

                using var convertedFrame = videoEncoder.CreateFrame();
                convertedFrame.MakeWritable();
                frameConverter.ConvertFrame(frame, convertedFrame);
                convertedFrame.Pts = frameIndex;

                foreach (var packet in videoEncoder.EncodeFrame(convertedFrame, packetRef))
                {
                    packet.RescaleTimestamp(videoEncoder.TimeBase, videoStream.TimeBase);
                    packet.StreamIndex = videoStream.Index;


                    formatContext.WritePacket(packet);
                }

                frameIndex++;
            }

            foreach (var packet in videoEncoder.EncodeFrame(null, packetRef))
            {
                packet.RescaleTimestamp(videoEncoder.TimeBase, videoStream.TimeBase);
                packet.StreamIndex = videoStream.Index;


                formatContext.WritePacket(packet);
            }

            #endregion


            formatContext.WriteTrailer();
        }
    }
}
