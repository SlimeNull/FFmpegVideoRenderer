using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.CSharp.RuntimeBinder;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace FFmpegVideoRenderer
{
    public class MediaSource : IDisposable
    {
        private readonly Stream _stream;
        private readonly IOContext _inputContext;
        private readonly FormatContext _inputFormatContext;
        private readonly MediaStream? _inputAudioStream;
        private readonly MediaStream? _inputVideoStream;
        private readonly CodecContext? _inputAudioDecoder;
        private readonly CodecContext? _inputVideoDecoder;

        private bool _disposedValue;

        private long _currentFrameTimeMilliseconds = 0;

        private VideoFrame? _currentVideoFrame;
        
        public int AudioFrameCacheSize { get; set; } = 2000;
        public int VideoFrameCacheSize { get; set; } = 50;

        public long MillisecondsToSeek { get; set; } = 1000;

        public long VideoFrameCount => _inputVideoStream?.NbFrames ?? throw new InvalidOperationException("No audio stream");
        public int VideoFrameWidth => _inputVideoDecoder?.Width ?? throw new InvalidOperationException("No video stream");
        public int VideoFrameHeight => _inputVideoDecoder?.Height ?? throw new InvalidOperationException("No video stream");



        public MediaSource(Stream stream)
        {
            _stream = stream;
            _inputContext = IOContext.ReadStream(stream);
            _inputFormatContext = FormatContext.OpenInputIO(_inputContext);

            // initialize
            _inputFormatContext.LoadStreamInfo();
            _inputAudioStream = _inputFormatContext.FindBestStreamOrNull(AVMediaType.Audio);
            _inputVideoStream = _inputFormatContext.FindBestStreamOrNull(AVMediaType.Video);

            if (_inputAudioStream != null)
            {
                _inputAudioDecoder = new CodecContext(Codec.FindDecoderById(_inputAudioStream.Value.Codecpar!.CodecId));
                _inputAudioDecoder.FillParameters(_inputAudioStream.Value.Codecpar);
                _inputAudioDecoder.Open();

                _inputAudioDecoder.ChLayout = _inputAudioDecoder.ChLayout;
            }

            if (_inputVideoStream != null)
            {
                _inputVideoDecoder = new CodecContext(Codec.FindDecoderById(_inputVideoStream.Value.Codecpar!.CodecId));
                _inputVideoDecoder.FillParameters(_inputVideoStream.Value.Codecpar);
                _inputVideoDecoder.Open();

                using var packet = new Packet();
                using var frame = new Frame();

                // initialize video decoder properties
                _inputFormatContext.ReadFrame(packet);
                _inputVideoDecoder.SendPacket(packet);
                _inputVideoDecoder.ReceiveFrame(frame);
            }
        }


        unsafe byte[] GetDataByteArrayFromFrame(Frame videoFrame)
        {
            var framePtr = videoFrame.Data[0];
            var rowPitch = videoFrame.Linesize[0];

            var height = videoFrame.Height;
            var data = new byte[rowPitch * height];

            fixed (byte* dataPtr = data)
            {
                for (int i = 0; i < height; i++)
                {
                    var offset = rowPitch * i;
                    NativeMemory.Copy((byte*)framePtr + offset, dataPtr + offset, (nuint)rowPitch);
                }
            }

            return data;
        }

        unsafe VideoFrame CreateVideoFrame(Frame originVideoFrame)
        {
            switch ((AVPixelFormat)originVideoFrame.Format)
            {
                case AVPixelFormat.Rgba:
                    return new VideoFrame(originVideoFrame.Width, originVideoFrame.Height, GetDataByteArrayFromFrame(originVideoFrame), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Rgba8888);
                case AVPixelFormat.Bgra:
                    return new VideoFrame(originVideoFrame.Width, originVideoFrame.Height, GetDataByteArrayFromFrame(originVideoFrame), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
            }

            using VideoFrameConverter converter = new VideoFrameConverter();
            using Frame convertedFrame = new Frame()
            {
                Width =  originVideoFrame.Width,
                Height = originVideoFrame.Height,
                Format = (int)AVPixelFormat.Bgra,
            };

            convertedFrame.EnsureBuffer();
            convertedFrame.MakeWritable();
            converter.ConvertFrame(originVideoFrame, convertedFrame);

            return new VideoFrame(convertedFrame.Width, convertedFrame.Height, GetDataByteArrayFromFrame(convertedFrame), convertedFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
        }

        unsafe IEnumerable<AudioSample> CreateAudioSamples(Frame originAudioFrame)
        {
            return [];
            //if (originAudioFrame.Format == (int)AVSampleFormat.Flt)
            //{

            //}
        }

        long GetTimestampFromMilliseconds(long milliseconds, AVRational timeBase)
        {
            return milliseconds * timeBase.Den / timeBase.Num / 1000;
        }

        long GetMillisecondsFromVideoTimestamp(long timestamp, AVRational timeBase)
        {
            return timestamp * 1000 * timeBase.Num / timeBase.Den;
        }

        void Decode(
            FormatContext inputFormatContext,
            CodecContext? videoDecoder,
            CodecContext? audioDecoder,
            int videoStreamIndex,
            int audioStreamIndex,
            long timeMilliseconds)
        {
            using var packet = new Packet();
            using var frame = new Frame();
            var codecResult = default(CodecResult);


            if (timeMilliseconds < _currentFrameTimeMilliseconds ||
                timeMilliseconds > _currentFrameTimeMilliseconds + MillisecondsToSeek ||
                !_currentVideoFrame.HasValue)
            {
                Debug.WriteLine("MediaSource Seek");

                if (videoDecoder is not null)
                {
                    var videoTimestamp = GetTimestampFromMilliseconds(timeMilliseconds, _inputVideoStream!.Value.TimeBase);
                    _inputFormatContext.SeekFrame(videoTimestamp, videoStreamIndex);
                }
                else if (audioDecoder is not null)
                {
                    var audioTimestamp = GetTimestampFromMilliseconds(timeMilliseconds, _inputAudioStream!.Value.TimeBase);
                    _inputFormatContext.SeekFrame(audioTimestamp, audioStreamIndex);
                }
            }

            while (true)
            {
                // receive all video frames
                if (videoDecoder is not null)
                {
                    while (true)
                    {
                        codecResult = videoDecoder.ReceiveFrame(frame);

                        var frameTimestamp = GetVideoFrameTimestamp(frame);
                        var frameMilliseconds = GetMillisecondsFromVideoTimestamp(frameTimestamp, _inputVideoStream!.Value.TimeBase);

                        if (codecResult == CodecResult.EOF)
                        {
                            return;
                        }

                        if (codecResult == CodecResult.Again)
                        {
                            break;
                        }

                        if (frameMilliseconds >= timeMilliseconds)
                        {
                            _currentVideoFrame = CreateVideoFrame(frame);
                            _currentFrameTimeMilliseconds = frameMilliseconds;
                            return;
                        }
                    }
                }

                // receive all audio frames
                if (audioDecoder is not null)
                {
                    while (true)
                    {
                        codecResult = audioDecoder.ReceiveFrame(frame);

                        //var frameTimestamp = GetVideoFrameTimestamp(frame);
                        //var frameMilliseconds = GetMillisecondsFromVideoTimestamp(frameTimestamp, _inputVideoStream!.Value.TimeBase);

                        if (codecResult == CodecResult.EOF)
                        {
                            return;
                        }

                        if (codecResult == CodecResult.Again)
                        {
                            break;
                        }

                        //if (frameMilliseconds >= timeMilliseconds)
                        //{
                        //    _currentVideoFrame = CreateVideoFrame(frame);
                        //    return;
                        //}
                    }
                }

                // read packets of one frame
                do
                {
                    codecResult = inputFormatContext.ReadFrame(packet);

                    if (packet.StreamIndex == videoStreamIndex && videoDecoder is not null)
                    {
                        videoDecoder.SendPacket(packet);
                    }
                    else if (packet.StreamIndex == audioStreamIndex && audioDecoder is not null)
                    {
                        audioDecoder.SendPacket(packet);
                    }
                }
                while (codecResult == CodecResult.Again);
            }
        }

        private long GetVideoFrameTimestamp(Frame frame)
        {
            return frame.BestEffortTimestamp;
            return frame.Pts * frame.TimeBase.Num / frame.TimeBase.Den;
        }

        public unsafe VideoFrame GetVideoFrame(long timeMilliseconds)
        {
            if (_inputVideoStream is null ||
                _inputVideoDecoder is null)
                throw new InvalidOperationException("No video stream");

            if (timeMilliseconds == _currentFrameTimeMilliseconds && 
                _currentVideoFrame.HasValue)
            {
                return _currentVideoFrame.Value;
            }

            Decode(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, _inputVideoStream?.Index ?? -1, _inputAudioStream?.Index ?? -1, timeMilliseconds);

            if (_currentVideoFrame.HasValue)
            {
                return _currentVideoFrame.Value;
            }

            throw new ArgumentException("Invalid time");
        }

        public AudioSample GetAudioSample(long timeMilliseconds)
        {
            if (_inputAudioStream is null ||
                _inputAudioDecoder is null)
                throw new InvalidOperationException("No audio stream");

            throw new NotImplementedException();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)

                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                _disposedValue = true;
            }
        }

        // TODO: 仅当“Dispose(bool disposing)”拥有用于释放未托管资源的代码时才替代终结器
        ~MediaSource()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // 不要更改此代码。请将清理代码放入“Dispose(bool disposing)”方法中
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        //internal record struct CachedAudioFrame(int Index, AudioSample)
        internal record struct CachedVideoFrame(int Index, VideoFrame Frame);
    }
}
