using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.CSharp.RuntimeBinder;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;
using SkiaSharp;

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
        private readonly VideoFrameConverter _videoFrameConverter;
        private readonly ArrayPool<byte> _videoDataArrayPool;
        private readonly ArrayPool<AudioSample> _audioDataArrayPool;

        private bool _disposedValue;

        private TimeSpan _currentVideoFrameTime = default;
        private TimeSpan _currentAudioFrameTime = default;

        private VideoFrame? _currentVideoFrame;
        private AudioFrame? _currentAudioFrame;

        //private Packet _decodePacket = new();
        //private Frame _decodeFrame = new();

        private Frame? _convertedVideoFrame;
        private SKBitmap? _videoFrameBitmap;

        public int AudioFrameCacheSize { get; set; } = 2000;
        public int VideoFrameCacheSize { get; set; } = 50;

        public TimeSpan TimeSpanToSeek { get; set; } = TimeSpan.FromMilliseconds(1000);
        public TimeSpan Duration { get; }

        public long VideoFrameCount => _inputVideoStream?.NbFrames ?? throw new InvalidOperationException("No audio stream");
        public int VideoFrameWidth => _inputVideoDecoder?.Width ?? throw new InvalidOperationException("No video stream");
        public int VideoFrameHeight => _inputVideoDecoder?.Height ?? throw new InvalidOperationException("No video stream");

        public bool HasAudio => _inputAudioDecoder is not null;
        public bool HasVideo => _inputVideoDecoder is not null;


        public MediaSource(Stream stream)
        {
            _stream = stream;
            _inputContext = IOContext.ReadStream(stream);
            _inputFormatContext = FormatContext.OpenInputIO(_inputContext);

            _videoDataArrayPool = ArrayPool<byte>.Create(5 * 1024 * 1024, 24);
            _audioDataArrayPool = ArrayPool<AudioSample>.Create(5 * 1024 * 1024, 24);

            // initialize
            _inputFormatContext.LoadStreamInfo();
            _inputAudioStream = _inputFormatContext.FindBestStreamOrNull(AVMediaType.Audio);
            _inputVideoStream = _inputFormatContext.FindBestStreamOrNull(AVMediaType.Video);
            _videoFrameConverter = new();

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

            TimeSpan duration = default;

            if (_inputVideoStream is not null)
            {
                // image or video
                if (_inputVideoStream.Value.Duration < 0)
                {
                    duration = TimeSpan.Zero;
                }
                else
                {
                    var videoDuration = TimeSpan.FromSeconds((double)_inputVideoStream.Value.Duration * _inputVideoStream.Value.TimeBase.Num / _inputVideoStream.Value.TimeBase.Den);
                    if (videoDuration > duration)
                        duration = videoDuration;
                }
            }

            if (_inputAudioStream is not null)
            {
                var audioDuration = TimeSpan.FromSeconds((double)_inputAudioStream.Value.Duration * _inputAudioStream.Value.TimeBase.Num / _inputAudioStream.Value.TimeBase.Den);
                if (audioDuration > duration)
                    duration = audioDuration;
            }

            Duration = duration;
        }


        unsafe byte[] GetDataByteArrayFromFrame(Frame videoFrame, ArrayPool<byte> arrayPool)
        {
            var framePtr = videoFrame.Data[0];
            var rowPitch = videoFrame.Linesize[0];

            var height = videoFrame.Height;
            var data = arrayPool.Rent(rowPitch * height);

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

        unsafe VideoFrame CreateVideoFrame(Frame originVideoFrame, ArrayPool<byte> arrayPool)
        {
            switch ((AVPixelFormat)originVideoFrame.Format)
            {
                case AVPixelFormat.Rgba:
                    return new VideoFrame(originVideoFrame.Width, originVideoFrame.Height, GetDataByteArrayFromFrame(originVideoFrame, arrayPool), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Rgba8888);
                case AVPixelFormat.Bgra:
                    return new VideoFrame(originVideoFrame.Width, originVideoFrame.Height, GetDataByteArrayFromFrame(originVideoFrame, arrayPool), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
            }

            if (_convertedVideoFrame is null)
            {
                _convertedVideoFrame = new Frame()
                {
                    Width = originVideoFrame.Width,
                    Height = originVideoFrame.Height,
                    Format = (int)AVPixelFormat.Bgra,
                };

                _convertedVideoFrame.EnsureBuffer();
                _convertedVideoFrame.MakeWritable();
            }

            _videoFrameConverter.ConvertFrame(originVideoFrame, _convertedVideoFrame);

            return new VideoFrame(_convertedVideoFrame.Width, _convertedVideoFrame.Height, GetDataByteArrayFromFrame(_convertedVideoFrame, arrayPool), _convertedVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
        }

        unsafe AudioFrame CreateAudioSamples(Frame originAudioFrame, ArrayPool<AudioSample> arrayPool, int sampleCount)
        {
            var channels = originAudioFrame.ChLayout.nb_channels;

            switch ((AVSampleFormat)originAudioFrame.Format)
            {
                case AVSampleFormat.Flt:
                {
                    var resultSampleCount = originAudioFrame.NbSamples / channels;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var dataPtr = (float*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex],
                            RightValue = dataPtr[dataIndex + 1],
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }

                case AVSampleFormat.S16:
                {
                    var resultSampleCount = originAudioFrame.NbSamples / channels;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var dataPtr = (short*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex] / (float)short.MaxValue,
                            RightValue = dataPtr[dataIndex + 1] / (float)short.MaxValue,
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }

                case AVSampleFormat.S32:
                {
                    var resultSampleCount = originAudioFrame.NbSamples / channels;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var dataPtr = (int*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex] / (float)int.MaxValue,
                            RightValue = dataPtr[dataIndex + 1] / (float)int.MaxValue,
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }

                case AVSampleFormat.Fltp:
                {
                    var resultSampleCount = originAudioFrame.NbSamples;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var leftDataPtr = (float*)originAudioFrame.Data[0];
                    var rightDataPtr = (float*)originAudioFrame.Data[1];
                    //var sampleCountPerPlane = frameSampleCount / channels;

                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex++, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = leftDataPtr[dataIndex],
                            RightValue = rightDataPtr[dataIndex],
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }

                case AVSampleFormat.S16p:
                {
                    var resultSampleCount = originAudioFrame.NbSamples;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var leftDataPtr = (short*)originAudioFrame.Data[0];
                    var rightDataPtr = (short*)originAudioFrame.Data[1];
                    //var sampleCountPerPlane = frameSampleCount / channels;

                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex++, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = leftDataPtr[dataIndex] / (float)short.MaxValue,
                            RightValue = rightDataPtr[dataIndex] / (float)short.MaxValue,
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }

                case AVSampleFormat.S32p:
                {
                    var resultSampleCount = originAudioFrame.NbSamples;
                    var samples = arrayPool.Rent(resultSampleCount);

                    var leftDataPtr = (int*)originAudioFrame.Data[0];
                    var rightDataPtr = (int*)originAudioFrame.Data[1];
                    //var sampleCountPerPlane = frameSampleCount / channels;

                    for (int dataIndex = 0, resultIndex = 0; dataIndex < sampleCount; dataIndex++, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = leftDataPtr[dataIndex] / (float)int.MaxValue,
                            RightValue = rightDataPtr[dataIndex] / (float)int.MaxValue,
                        };
                    }

                    return new AudioFrame(samples, resultSampleCount);
                }
            }

            throw new NotSupportedException();
        }

        long GetTimestampFromTime(TimeSpan milliseconds, AVRational timeBase)
        {
            return (long)milliseconds.TotalSeconds * timeBase.Den / timeBase.Num;
        }

        TimeSpan GetTimeFromStreamTimestamp(long timestamp, AVRational timeBase)
        {
            return TimeSpan.FromSeconds((double)timestamp * timeBase.Num / timeBase.Den);
        }

        TimeSpan GetAudioFrameDuration(AudioFrame frame, float sampleRate, int channelCount)
        {
            return TimeSpan.FromSeconds(frame.SampleCount / sampleRate / channelCount);
        }

        bool GetAudioSampleFromFrame(AudioFrame frame, float sampleRate, int channelCount, TimeSpan timeOffset, out AudioSample audioSample)
        {
            var index = (int)(timeOffset.TotalSeconds * sampleRate);
            if (index >= 0 && index < frame.SampleCount)
            {
                audioSample = frame.Samples[index];
                return true;
            }

            audioSample = default;
            return false;
        }

        unsafe void Decode(
            FormatContext inputFormatContext,
            CodecContext? videoDecoder,
            CodecContext? audioDecoder,
            AVMediaType mediaType,
            int videoStreamIndex,
            int audioStreamIndex,
            TimeSpan time)
        {
            var codecResult = default(CodecResult);
            using var packet = new Packet();
            using var frame = new Frame();

            try
            {
                if (mediaType == AVMediaType.Video)
                {
                    if (time < _currentVideoFrameTime ||
                        time > _currentVideoFrameTime + TimeSpanToSeek ||
                        !_currentVideoFrame.HasValue)
                    {

                        if (videoDecoder is not null)
                        {
                            var videoTimestamp = GetTimestampFromTime(time, _inputVideoStream!.Value.TimeBase);
                            _inputFormatContext.SeekFrame(videoTimestamp, videoStreamIndex);
                        }
                        else if (audioDecoder is not null)
                        {
                            var audioTimestamp = GetTimestampFromTime(time, _inputAudioStream!.Value.TimeBase);
                            _inputFormatContext.SeekFrame(audioTimestamp, audioStreamIndex);
                        }

                        if (_inputAudioDecoder is not null)
                        {
                            ffmpeg.avcodec_flush_buffers(_inputAudioDecoder);
                        }

                        if (_inputVideoDecoder is not null)
                        {
                            ffmpeg.avcodec_flush_buffers(_inputVideoDecoder);
                        }
                    }
                }
                else if (mediaType == AVMediaType.Audio)
                {
                    if (time < _currentAudioFrameTime ||
                        time > _currentAudioFrameTime + TimeSpanToSeek ||
                        !_currentAudioFrame.HasValue)
                    {
                        if (audioDecoder is not null)
                        {
                            var audioTimestamp = GetTimestampFromTime(time, _inputAudioStream!.Value.TimeBase);
                            _inputFormatContext.SeekFrame(audioTimestamp, audioStreamIndex);
                        }
                        else if (videoDecoder is not null)
                        {
                            var videoTimestamp = GetTimestampFromTime(time, _inputVideoStream!.Value.TimeBase);
                            _inputFormatContext.SeekFrame(videoTimestamp, videoStreamIndex);
                        }

                        if (_inputAudioDecoder is not null)
                        {
                            ffmpeg.avcodec_flush_buffers(_inputAudioDecoder);
                        }

                        if (_inputVideoDecoder is not null)
                        {
                            ffmpeg.avcodec_flush_buffers(_inputVideoDecoder);
                        }
                    }
                }

                while (true)
                {
                    // receive all audio frames
                    if (audioDecoder is not null)
                    {
                        while (true)
                        {
                            codecResult = audioDecoder.ReceiveFrame(frame);

                            if (codecResult == CodecResult.EOF)
                            {
                                return;
                            }

                            if (codecResult == CodecResult.Again)
                            {
                                break;
                            }

                            var frameTimestamp = GetVideoFrameTimestamp(frame);
                            var frameTime = GetTimeFromStreamTimestamp(frameTimestamp, _inputAudioStream!.Value.TimeBase);
                            var frameDuration = TimeSpan.FromSeconds((double)frame.NbSamples / frame.SampleRate * frame.ChLayout.nb_channels);

                            // free array renting
                            if (_currentAudioFrame.HasValue &&
                                _currentAudioFrame.Value.Samples != null)
                            {
                                _audioDataArrayPool.Return(_currentAudioFrame.Value.Samples);
                            }

                            _currentAudioFrame = CreateAudioSamples(frame, _audioDataArrayPool, frame.NbSamples);
                            _currentAudioFrameTime = frameTime;

                            if (frameTime + frameDuration >= time)
                            {
                                return;
                            }
                        }
                    }

                    // receive all video frames
                    if (videoDecoder is not null)
                    {
                        while (true)
                        {
                            codecResult = videoDecoder.ReceiveFrame(frame);

                            if (codecResult == CodecResult.EOF)
                            {
                                return;
                            }

                            if (codecResult == CodecResult.Again)
                            {
                                break;
                            }

                            var frameTimestamp = GetVideoFrameTimestamp(frame);
                            var frameTime = GetTimeFromStreamTimestamp(frameTimestamp, _inputVideoStream!.Value.TimeBase);

                            if (_currentVideoFrame.HasValue &&
                                _currentVideoFrame.Value.Data != null)
                            {
                                _videoDataArrayPool.Return(_currentVideoFrame.Value.Data);
                            }

                            _currentVideoFrame = CreateVideoFrame(frame, _videoDataArrayPool);
                            _currentVideoFrameTime = frameTime;
                            if (frameTime >= time && mediaType == AVMediaType.Video)
                            {
                                return;
                            }
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
            finally
            {
                packet.Free();
                frame.Free();
            }
        }

        private long GetVideoFrameTimestamp(Frame frame)
        {
            return frame.BestEffortTimestamp;
        }

        public unsafe VideoFrame? GetVideoFrame(TimeSpan time)
        {
            if (_inputVideoStream is null ||
                _inputVideoDecoder is null)
                throw new InvalidOperationException("No video stream");

            if (Duration == default)
            {
                time = default;
            }

            if (time > Duration)
            {
                return null;
            }

            if (_currentVideoFrame.HasValue &&
                time == _currentVideoFrameTime)
            {
                return _currentVideoFrame.Value;
            }

            Decode(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, AVMediaType.Video, _inputVideoStream?.Index ?? -1, _inputAudioStream?.Index ?? -1, time);

            if (_currentVideoFrame.HasValue)
            {
                return _currentVideoFrame.Value;
            }

            return null;
        }

        public SKBitmap? GetVideoFrameBitmap(TimeSpan time)
        {
            if (GetVideoFrame(time) is VideoFrame frame)
            {
                if (_videoFrameBitmap is null)
                {
                    _videoFrameBitmap = new SKBitmap(VideoFrameWidth, VideoFrameHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                }

                frame.FillBitmap(_videoFrameBitmap);

                return _videoFrameBitmap;
            }

            return null;
        }

        public AudioSample? GetAudioSample(TimeSpan time)
        {
            if (_inputAudioStream is null ||
                _inputAudioDecoder is null)
                throw new InvalidOperationException("No audio stream");

            if (time > Duration)
            {
                return null;
            }

            if (_currentAudioFrame.HasValue &&
                time >= _currentAudioFrameTime &&
                GetAudioSampleFromFrame(_currentAudioFrame.Value, _inputAudioDecoder.SampleRate, _inputAudioDecoder.ChLayout.nb_channels, time - _currentAudioFrameTime, out var sample))
            {
                return sample;
            }

            Decode(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, AVMediaType.Audio, _inputVideoStream?.Index ?? -1, _inputAudioStream?.Index ?? -1, time);

            if (_currentAudioFrame.HasValue &&
                time >= _currentAudioFrameTime &&
                GetAudioSampleFromFrame(_currentAudioFrame.Value, _inputAudioDecoder.SampleRate, _inputAudioDecoder.ChLayout.nb_channels, time - _currentAudioFrameTime, out sample))
            {
                return sample;
            }

            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)

                    _inputFormatContext.Dispose();
                    _inputVideoDecoder?.Dispose();
                    _inputAudioDecoder?.Dispose();

                }

                // TODO: 释放未托管的资源(未托管的对象)并重写终结器
                // TODO: 将大型字段设置为 null
                _currentVideoFrame = null;
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
