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

        private long _currentFrameTimeMilliseconds = 0;

        private VideoFrame? _currentVideoFrame;
        private AudioFrame? _currentAudioFrame;

        //private Packet _decodePacket = new();
        //private Frame _decodeFrame = new();

        private Frame? _convertedVideoFrame;

        public int AudioFrameCacheSize { get; set; } = 2000;
        public int VideoFrameCacheSize { get; set; } = 50;

        public long MillisecondsToSeek { get; set; } = 1000;
        public long MillisecondsDuration { get; }

        public long VideoFrameCount => _inputVideoStream?.NbFrames ?? throw new InvalidOperationException("No audio stream");
        public int VideoFrameWidth => _inputVideoDecoder?.Width ?? throw new InvalidOperationException("No video stream");
        public int VideoFrameHeight => _inputVideoDecoder?.Height ?? throw new InvalidOperationException("No video stream");



        public MediaSource(Stream stream)
        {
            _stream = stream;
            _inputContext = IOContext.ReadStream(stream);
            _inputFormatContext = FormatContext.OpenInputIO(_inputContext);

            _videoDataArrayPool = ArrayPool<byte>.Create();
            _audioDataArrayPool = ArrayPool<AudioSample>.Create();

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

            var millisecondsDuration = (long)0;

            if (_inputVideoStream is not null)
            {
                var videoDuration = _inputVideoStream.Value.Duration * 1000 * _inputVideoStream.Value.TimeBase.Num / _inputVideoStream.Value.TimeBase.Den;
                if (videoDuration > millisecondsDuration)
                    millisecondsDuration = videoDuration;
            }

            if (_inputAudioStream is not null)
            {
                var audioDuration = _inputAudioStream.Value.Duration * 1000 * _inputAudioStream.Value.TimeBase.Num / _inputAudioStream.Value.TimeBase.Den;
                if (audioDuration > millisecondsDuration)
                    millisecondsDuration = audioDuration;
            }

            MillisecondsDuration = millisecondsDuration;
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

        unsafe AudioFrame CreateAudioSamples(Frame originAudioFrame, ArrayPool<AudioSample> arrayPool, int frameSampleCount)
        {
            var channels = originAudioFrame.ChLayout.nb_channels;
            var resultSampleCount = frameSampleCount / channels;
            var samples = arrayPool.Rent(resultSampleCount);

            switch ((AVSampleFormat)originAudioFrame.Format)
            {
                case AVSampleFormat.Flt:
                {
                    var dataPtr = (float*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < frameSampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex],
                            RightValue = dataPtr[dataIndex + 1],
                        };
                    }

                    break;
                }

                case AVSampleFormat.S16:
                {
                    var dataPtr = (short*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < frameSampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex] / (float)short.MaxValue,
                            RightValue = dataPtr[dataIndex + 1] / (float)short.MaxValue,
                        };
                    }

                    break;
                }

                case AVSampleFormat.S32:
                {
                    var dataPtr = (int*)originAudioFrame.Data[0];
                    for (int dataIndex = 0, resultIndex = 0; dataIndex < frameSampleCount; dataIndex += channels, resultIndex++)
                    {
                        samples[resultIndex] = new AudioSample()
                        {
                            LeftValue = dataPtr[dataIndex] / (float)int.MaxValue,
                            RightValue = dataPtr[dataIndex + 1] / (float)int.MaxValue,
                        };
                    }

                    break;
                }
            }

            return new AudioFrame(samples, resultSampleCount);
        }

        long GetTimestampFromMilliseconds(long milliseconds, AVRational timeBase)
        {
            return milliseconds * timeBase.Den / timeBase.Num / 1000;
        }

        long GetMillisecondsFromVideoTimestamp(long timestamp, AVRational timeBase)
        {
            return timestamp * 1000 * timeBase.Num / timeBase.Den;
        }

        long GetAudioFrameMillisecondsDuration(AudioFrame frame, float sampleRate, int channelCount)
        {
            return (long)(frame.SampleCount * 1000 / sampleRate / channelCount);
        }

        bool GetAudioSampleFromFrame(AudioFrame frame, float sampleRate, int channelCount, long timeOffsetMilliseconds, out AudioSample audioSample)
        {
            var index = (int)(timeOffsetMilliseconds * sampleRate / channelCount / 1000);
            if (index >= 0 && index < frame.SampleCount)
            {
                audioSample = frame.Samples[index];
                return true;
            }

            audioSample = default;
            return false;
        }

        void Decode(
            FormatContext inputFormatContext,
            CodecContext? videoDecoder,
            CodecContext? audioDecoder,
            AVMediaType mediaType,
            int videoStreamIndex,
            int audioStreamIndex,
            long timeMilliseconds)
        {
            var codecResult = default(CodecResult);
            using var packet = new Packet();
            using var frame = new Frame();

            try
            {
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

                            if (_currentVideoFrame.HasValue &&
                                _currentVideoFrame.Value.Data != null)
                            {
                                _videoDataArrayPool.Return(_currentVideoFrame.Value.Data);
                            }

                            _currentVideoFrame = CreateVideoFrame(frame, _videoDataArrayPool);
                            _currentFrameTimeMilliseconds = frameMilliseconds;
                            if (frameMilliseconds >= timeMilliseconds && mediaType == AVMediaType.Video)
                            {
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

                            var frameTimestamp = GetVideoFrameTimestamp(frame);
                            var frameMilliseconds = GetMillisecondsFromVideoTimestamp(frameTimestamp, _inputAudioStream!.Value.TimeBase);

                            if (codecResult == CodecResult.EOF)
                            {
                                return;
                            }

                            if (codecResult == CodecResult.Again)
                            {
                                break;
                            }

                            // free array renting
                            if (_currentAudioFrame.HasValue &&
                                _currentAudioFrame.Value.Samples != null)
                            {
                                _audioDataArrayPool.Return(_currentAudioFrame.Value.Samples);
                            }

                            _currentAudioFrame = CreateAudioSamples(frame, _audioDataArrayPool, audioDecoder.FrameSize);
                            _currentFrameTimeMilliseconds = frameMilliseconds;
                            if (frameMilliseconds >= timeMilliseconds)
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

        public unsafe VideoFrame? GetVideoFrame(long timeMilliseconds)
        {
            if (_inputVideoStream is null ||
                _inputVideoDecoder is null)
                throw new InvalidOperationException("No video stream");

            if (timeMilliseconds > MillisecondsDuration)
            {
                return null;
            }

            if (_currentVideoFrame.HasValue &&
                timeMilliseconds == _currentFrameTimeMilliseconds)
            {
                return _currentVideoFrame.Value;
            }

            Decode(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, AVMediaType.Video, _inputVideoStream?.Index ?? -1, _inputAudioStream?.Index ?? -1, timeMilliseconds);

            if (_currentVideoFrame.HasValue)
            {
                return _currentVideoFrame.Value;
            }

            throw new ArgumentException("Invalid time");
        }

        public AudioSample? GetAudioSample(long timeMilliseconds)
        {
            if (_inputAudioStream is null ||
                _inputAudioDecoder is null)
                throw new InvalidOperationException("No audio stream");

            if (timeMilliseconds > MillisecondsDuration)
            {
                return null;
            }

            if (_currentAudioFrame.HasValue &&
                timeMilliseconds >= _currentFrameTimeMilliseconds &&
                GetAudioSampleFromFrame(_currentAudioFrame.Value, _inputAudioDecoder.SampleRate, _inputAudioDecoder.ChLayout.nb_channels, timeMilliseconds - _currentFrameTimeMilliseconds, out var sample))
            {
                return sample;
            }

            Decode(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, AVMediaType.Audio, _inputVideoStream?.Index ?? -1, _inputAudioStream?.Index ?? -1, timeMilliseconds);

            if (_currentAudioFrame.HasValue &&
                timeMilliseconds >= _currentFrameTimeMilliseconds &&
                GetAudioSampleFromFrame(_currentAudioFrame.Value, _inputAudioDecoder.SampleRate, _inputAudioDecoder.ChLayout.nb_channels, timeMilliseconds - _currentFrameTimeMilliseconds, out sample))
            {
                return sample;
            }

            throw new ArgumentException("Invalid time");
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
