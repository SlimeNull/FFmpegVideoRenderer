using System.Runtime.InteropServices;
using System.Xml.Linq;
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
        private readonly MediaStream _inputAudioStream;
        private readonly MediaStream _inputVideoStream;
        private readonly CodecContext? _inputAudioDecoder;
        private readonly CodecContext? _inputVideoDecoder;
        private readonly List<VideoFrame> _decodedVideoFrames = new();
        private readonly List<AudioSample> _decodedAudioSamples = new();
        private bool _disposedValue;

        public long VideoFrameCount => _inputVideoStream.NbFrames;
        public int VideoFrameWidth => _inputVideoDecoder?.Width ?? throw new InvalidOperationException("No video stream");
        public int VideoFrameHeight => _inputVideoDecoder?.Height ?? throw new InvalidOperationException("No video stream");



        public MediaSource(Stream stream)
        {
            _stream = stream;
            _inputContext = IOContext.ReadStream(stream);
            _inputFormatContext = FormatContext.OpenInputIO(_inputContext);

            // initialize
            _inputFormatContext.LoadStreamInfo();
            _inputAudioStream = _inputFormatContext.GetAudioStream();
            _inputVideoStream = _inputFormatContext.GetVideoStream();

            if (_inputAudioStream.Index != -1)
            {
                _inputAudioDecoder = new CodecContext(Codec.FindDecoderById(_inputAudioStream.Codecpar!.CodecId));
                _inputAudioDecoder.FillParameters(_inputAudioStream.Codecpar);
                _inputAudioDecoder.Open();

                _inputAudioDecoder.ChLayout = _inputAudioDecoder.ChLayout;
            }

            if (_inputVideoStream.Index != -1)
            {
                _inputVideoDecoder = new CodecContext(Codec.FindDecoderById(_inputVideoStream.Codecpar!.CodecId));
                _inputVideoDecoder.FillParameters(_inputVideoStream.Codecpar);
                _inputVideoDecoder.Open();
            }
        }


        int GetVideoFrameIndex(long timeMilliseconds, AVRational frameRate)
        {
            return (int)(timeMilliseconds * frameRate.Den / frameRate.Num / 1000);
        }

        int GetAudioSampleIndex(long timeMilliseconds, AVRational frameRate)
        {
            throw new NotImplementedException();
        }

        unsafe byte[] GetDataFromFrame(Frame videoFrame)
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
                    return new VideoFrame(GetDataFromFrame(originVideoFrame), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Rgba8888);
                case AVPixelFormat.Bgra:
                    return new VideoFrame(GetDataFromFrame(originVideoFrame), originVideoFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
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

            return new VideoFrame(GetDataFromFrame(convertedFrame), convertedFrame.Linesize[0], 4, SkiaSharp.SKColorType.Bgra8888);
        }

        unsafe IEnumerable<AudioSample> CreateAudioSamples(Frame originAudioFrame)
        {
            return [];
            //if (originAudioFrame.Format == (int)AVSampleFormat.Flt)
            //{

            //}
        }

        void EnsureDecoded(
            FormatContext inputFormatContext,
            CodecContext? videoDecoder,
            CodecContext? audioDecoder,
            int videoFrameCount,
            int audioSampleCount,
            int videoStreamIndex,
            int audioStreamIndex)
        {
            using var packet = new Packet();
            using var frame = new Frame();
            var codecResult = default(CodecResult);

            while (
                _decodedVideoFrames.Count < videoFrameCount ||
                _decodedAudioSamples.Count < audioSampleCount)
            {
                // try receive frame
                if (videoDecoder is not null)
                {
                    codecResult = videoDecoder.ReceiveFrame(frame);
                    if (codecResult == CodecResult.Success)
                    {
                        _decodedVideoFrames.Add(CreateVideoFrame(frame));
                    }
                }

                if (audioDecoder is not null)
                {
                    codecResult = audioDecoder.ReceiveFrame(frame);
                    if (codecResult == CodecResult.Success)
                    {
                        _decodedAudioSamples.AddRange(CreateAudioSamples(frame));
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

                // eof, break
                if (codecResult == CodecResult.EOF)
                {
                    break;
                }

                // decode
                if (videoDecoder is not null)
                {
                    codecResult = videoDecoder.ReceiveFrame(frame);
                    if (codecResult == CodecResult.Success)
                    {
                        _decodedVideoFrames.Add(CreateVideoFrame(frame));
                    }
                }

                if (audioDecoder is not null)
                {
                    codecResult = audioDecoder.ReceiveFrame(frame);
                    if (codecResult == CodecResult.Success)
                    {
                        _decodedAudioSamples.AddRange(CreateAudioSamples(frame));
                    }
                }
            }
        }

        public VideoFrame GetVideoFrame(long timeMilliseconds)
        {
            if (_inputVideoDecoder is null)
                throw new InvalidOperationException("No video stream");

            var targetFrameIndex = GetVideoFrameIndex(timeMilliseconds, _inputVideoStream.RFrameRate);

            EnsureDecoded(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, targetFrameIndex + 1, 0, _inputVideoStream.Index, _inputAudioStream.Index);

            if (targetFrameIndex >= _decodedVideoFrames.Count)
            {
                targetFrameIndex = _decodedVideoFrames.Count - 1;
            }

            return _decodedVideoFrames[targetFrameIndex];
        }

        public AudioSample GetAudioSample(long timeMilliseconds)
        {
            if (_inputVideoDecoder is null)
                throw new InvalidOperationException("No video stream");

            var targetSampleIndex = GetAudioSampleIndex(timeMilliseconds, _inputAudioStream.RFrameRate);

            EnsureDecoded(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, 0, targetSampleIndex + 1, _inputVideoStream.Index, _inputAudioStream.Index);

            if (targetSampleIndex >= _decodedAudioSamples.Count)
            {
                targetSampleIndex = _decodedAudioSamples.Count - 1;
            }

            return _decodedAudioSamples[targetSampleIndex];
        }

        public int GetVideoFrameIndex(long timeMilliseconds)
        {
            return GetVideoFrameIndex(timeMilliseconds, _inputVideoStream.RFrameRate);
        }

        public int GetAudioSampleIndex(long timeMilliseconds)
        {
            return GetAudioSampleIndex(timeMilliseconds, default);
        }

        public VideoFrame GetVideoFrameFromIndex(int index)
        {
            EnsureDecoded(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, index + 1, 0, _inputVideoStream.Index, _inputAudioStream.Index);

            if (index >= _decodedVideoFrames.Count)
            {
                index = _decodedVideoFrames.Count - 1;
            }

            return _decodedVideoFrames[index];
        }

        public AudioSample GetAudioSampleFromIndex(int index)
        {
            EnsureDecoded(_inputFormatContext, _inputVideoDecoder, _inputAudioDecoder, 0, index + 1, _inputVideoStream.Index, _inputAudioStream.Index);

            if (index >= _decodedAudioSamples.Count)
            {
                index = _decodedAudioSamples.Count - 1;
            }

            return _decodedAudioSamples[index];
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
    }
}
