using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LumINE.Utils
{
    // APNG frame extraction. PNG decompression is delegated to Windows' PNG decoder.
    public sealed class ApngFrame
    {
        public int Width, Height, X, Y, DelayMs, Dispose, Blend;
        internal readonly List<byte[]> Data = new List<byte[]>();
    }

    public sealed class ApngAnimation
    {
        public int Width, Height;
        public uint Plays;
        public readonly List<ApngFrame> Frames = new List<ApngFrame>();
        private byte[] _header;
        private readonly List<byte[]> _shared = new List<byte[]>();
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static ApngAnimation Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 33 || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("PNG size is invalid.");
            for (int i = 0; i < 8; i++) if (bytes[i] != Signature[i]) throw new InvalidDataException("Not a PNG.");
            var animation = new ApngAnimation();
            ApngFrame frame = null;
            uint declared = 0, sequence = 0;
            bool hasData = false, ended = false;
            for (int offset = 8; offset < bytes.Length;)
            {
                if (bytes.Length - offset < 12) throw new InvalidDataException("Truncated PNG chunk.");
                uint rawLength = U32(bytes, offset);
                if (rawLength > (uint)(bytes.Length - offset - 12)) throw new InvalidDataException("Truncated PNG payload.");
                int length = (int)rawLength, data = offset + 8;
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                if (Crc(bytes, offset + 4, length + 4) != U32(bytes, data + length)) throw new InvalidDataException("PNG CRC mismatch.");
                if (animation._header == null && type != "IHDR") throw new InvalidDataException("Missing IHDR.");
                if (type == "IHDR")
                {
                    if (length != 13 || animation._header != null) throw new InvalidDataException("Invalid IHDR.");
                    animation._header = Copy(bytes, data, length);
                    animation.Width = Dimension(U32(bytes, data)); animation.Height = Dimension(U32(bytes, data + 4));
                }
                else if (type == "acTL")
                {
                    if (length != 8 || declared != 0 || hasData) throw new InvalidDataException("Invalid acTL.");
                    declared = U32(bytes, data); animation.Plays = U32(bytes, data + 4);
                    if (declared == 0 || declared > 200) throw new InvalidDataException("Too many APNG frames.");
                }
                else if (type == "fcTL")
                {
                    if (length != 26 || declared == 0 || U32(bytes, data) != sequence++) throw new InvalidDataException("Invalid APNG sequence.");
                    if (frame != null && frame.Data.Count == 0) throw new InvalidDataException("Empty APNG frame.");
                    frame = new ApngFrame { Width = Dimension(U32(bytes, data + 4)), Height = Dimension(U32(bytes, data + 8)),
                        X = checked((int)U32(bytes, data + 12)), Y = checked((int)U32(bytes, data + 16)), Dispose = bytes[data + 24], Blend = bytes[data + 25] };
                    int denominator = bytes[data + 22] * 256 + bytes[data + 23];
                    if (denominator == 0) denominator = 100;
                    frame.DelayMs = Math.Max(20, (int)Math.Round(1000.0 * (bytes[data + 20] * 256 + bytes[data + 21]) / denominator));
                    if (frame.X > animation.Width - frame.Width || frame.Y > animation.Height - frame.Height || frame.Dispose > 2 || frame.Blend > 1)
                        throw new InvalidDataException("Invalid APNG rectangle or operation.");
                    if (animation.Frames.Count == 0 && frame.Dispose == 2) frame.Dispose = 1;
                    animation.Frames.Add(frame);
                    if (animation.Frames.Count > declared) throw new InvalidDataException("APNG frame count mismatch.");
                }
                else if (type == "IDAT")
                {
                    if (frame != null)
                    {
                        if (animation.Frames.Count != 1 || frame.X != 0 || frame.Y != 0 || frame.Width != animation.Width || frame.Height != animation.Height)
                            throw new InvalidDataException("Invalid APNG default frame.");
                        frame.Data.Add(Copy(bytes, data, length));
                    }
                    hasData = true;
                }
                else if (type == "fdAT")
                {
                    if (frame == null || length < 4 || U32(bytes, data) != sequence++) throw new InvalidDataException("Invalid fdAT.");
                    frame.Data.Add(Copy(bytes, data + 4, length - 4));
                }
                else if (type == "IEND") { if (length != 0) throw new InvalidDataException("Invalid IEND."); ended = true; break; }
                else if (!hasData && (type == "PLTE" || type == "tRNS" || type == "gAMA" || type == "sRGB" || type == "iCCP" || type == "cHRM" || type == "sBIT"))
                    animation._shared.Add(Copy(bytes, offset, length + 12));
                offset += length + 12;
            }
            if (!ended || !hasData) throw new InvalidDataException("Incomplete PNG.");
            if (declared == 0) return null;
            if (animation.Frames.Count != declared || frame == null || frame.Data.Count == 0) throw new InvalidDataException("Incomplete APNG.");
            return animation;
        }

        public byte[] FramePng(ApngFrame frame)
        {
            using (var stream = new MemoryStream())
            {
                stream.Write(Signature, 0, 8);
                var header = (byte[])_header.Clone();
                Put32(header, 0, (uint)frame.Width); Put32(header, 4, (uint)frame.Height);
                WriteChunk(stream, "IHDR", header);
                foreach (var shared in _shared) stream.Write(shared, 0, shared.Length);
                foreach (var data in frame.Data) WriteChunk(stream, "IDAT", data);
                WriteChunk(stream, "IEND", new byte[0]);
                return stream.ToArray();
            }
        }

        private static int Dimension(uint value) { if (value == 0 || value > 1024) throw new InvalidDataException("PNG dimensions exceed playback limits."); return (int)value; }
        private static byte[] Copy(byte[] bytes, int start, int length) { var result = new byte[length]; Buffer.BlockCopy(bytes, start, result, 0, length); return result; }
        private static uint U32(byte[] bytes, int offset) { return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3]; }
        private static void Put32(byte[] bytes, int offset, uint value) { for (int i = 3; i >= 0; i--) { bytes[offset + i] = (byte)value; value >>= 8; } }
        private static uint Crc(byte[] bytes, int offset, int length)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < length; i++) { crc ^= bytes[offset + i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            return crc ^ 0xffffffff;
        }
        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var chunk = new byte[data.Length + 12];
            Put32(chunk, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4); data.CopyTo(chunk, 8);
            Put32(chunk, data.Length + 8, Crc(chunk, 4, data.Length + 4));
            stream.Write(chunk, 0, chunk.Length);
        }
    }

    // Pixels are premultiplied BGRA8, matching BitmapDecoder and WriteableBitmap.
    public sealed class ApngCanvas
    {
        public byte[] Pixels { get; private set; }
        private readonly int _width;
        private ApngFrame _previous;
        private byte[] _restore;
        public ApngCanvas(int width, int height) { _width = width; Pixels = new byte[checked(width * height * 4)]; }
        public void Render(ApngFrame frame, byte[] pixels)
        {
            if (pixels.Length != frame.Width * frame.Height * 4) throw new InvalidDataException("Unexpected decoded frame size.");
            if (_previous != null)
            {
                if (_previous.Dispose == 1)
                    for (int y = 0; y < _previous.Height; y++) Array.Clear(Pixels, ((_previous.Y + y) * _width + _previous.X) * 4, _previous.Width * 4);
                else if (_previous.Dispose == 2 && _restore != null) Buffer.BlockCopy(_restore, 0, Pixels, 0, Pixels.Length);
            }
            _restore = frame.Dispose == 2 ? (byte[])Pixels.Clone() : null;
            for (int y = 0; y < frame.Height; y++) for (int x = 0; x < frame.Width; x++)
            {
                int source = (y * frame.Width + x) * 4, target = ((frame.Y + y) * _width + frame.X + x) * 4;
                if (frame.Blend == 0) Buffer.BlockCopy(pixels, source, Pixels, target, 4);
                else
                {
                    int inverse = 255 - pixels[source + 3];
                    for (int channel = 0; channel < 4; channel++)
                        Pixels[target + channel] = (byte)Math.Min(255, pixels[source + channel] + (Pixels[target + channel] * inverse + 127) / 255);
                }
            }
            _previous = frame;
        }
    }
}
