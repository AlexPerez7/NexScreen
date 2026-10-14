using NexScreen.Host.Video;

namespace NexScreen.Host.Tests;

public class FlvReaderTests
{
    private static readonly byte[] Sps = [0x67, 0x4D, 0x40, 0x28, 0xAA];
    private static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0x80];
    private static readonly byte[] IdrSlice = [0x65, 0x88, 0x84, 0x00, 0x33];
    private static readonly byte[] Sei = [0x06, 0x05, 0x01, 0xFF];

    [Fact]
    public async Task ReadsSequenceHeaderAndFramesAsAnnexB()
    {
        var flv = new FlvBuilder()
            .AudioTag()
            .VideoTag(frameType: 1, packetType: 0, timestampMs: 0, DecoderConfig(Sps, Pps))
            .VideoTag(frameType: 1, packetType: 1, timestampMs: 0, Avcc(Sei, IdrSlice))
            .VideoTag(frameType: 2, packetType: 1, timestampMs: 33, Avcc(new byte[] { 0x41, 0x9A }))
            .Build();

        var frames = await ReadAllAsync(flv);

        Assert.Equal(3, frames.Count);

        Assert.Equal(FrameFlags.Config, frames[0].Flags);
        Assert.Equal(AnnexB(Sps, Pps), frames[0].Data);

        Assert.Equal(FrameFlags.KeyFrame, frames[1].Flags);
        Assert.Equal(AnnexB(Sei, IdrSlice), frames[1].Data);
        Assert.Equal(0, frames[1].PtsUs);

        Assert.Equal(FrameFlags.None, frames[2].Flags);
        Assert.Equal(33_000, frames[2].PtsUs);
    }

    [Fact]
    public async Task StopsCleanlyOnTruncatedStream()
    {
        var flv = new FlvBuilder()
            .VideoTag(frameType: 1, packetType: 0, timestampMs: 0, DecoderConfig(Sps, Pps))
            .Build();

        // Quita el PreviousTagSize final y los últimos bytes del tag.
        var frames = await ReadAllAsync(flv[..^8]);

        Assert.Empty(frames);
    }

    [Fact]
    public void RejectsNalLengthBeyondPacket()
    {
        var reader = new FlvReader(Stream.Null);
        byte[] tag = [0x17, 0x01, 0, 0, 0, 0, 0, 0, 0x10, 0x65];

        Assert.Throws<InvalidDataException>(() => reader.ParseVideoTag(tag, 0));
    }

    [Fact]
    public void AppliesNegativeCompositionTime()
    {
        var reader = new FlvReader(Stream.Null);
        byte[] tag = [0x27, 0x01, 0xFF, 0xFF, 0xF6, 0, 0, 0, 2, 0x41, 0x9A];

        var frame = reader.ParseVideoTag(tag, timestampMs: 100);

        Assert.Equal(90_000, frame!.Value.PtsUs);
    }

    [Fact]
    public async Task RejectsNonFlvInput()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync([0x00, 0x00, 0x00, 0x01, 0x67, 0, 0, 0, 0]));
    }

    private static async Task<List<EncodedFrame>> ReadAllAsync(byte[] data)
    {
        var frames = new List<EncodedFrame>();
        await foreach (var frame in new FlvReader(new MemoryStream(data)).ReadFramesAsync(CancellationToken.None))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static byte[] DecoderConfig(byte[] sps, byte[] pps) =>
    [
        0x01, sps[1], sps[2], sps[3], 0xFF, 0xE1,
        (byte)(sps.Length >> 8), (byte)sps.Length, .. sps,
        0x01,
        (byte)(pps.Length >> 8), (byte)pps.Length, .. pps,
    ];

    private static byte[] Avcc(params byte[][] nals) =>
        nals.SelectMany(n => new byte[] { 0, 0, (byte)(n.Length >> 8), (byte)n.Length }.Concat(n)).ToArray();

    private static byte[] AnnexB(params byte[][] nals) =>
        nals.SelectMany(n => new byte[] { 0, 0, 0, 1 }.Concat(n)).ToArray();

    private sealed class FlvBuilder
    {
        private readonly MemoryStream _stream = new();
        private uint _previousTagSize;

        public FlvBuilder()
        {
            _stream.Write("FLV"u8);
            _stream.Write([0x01, 0x01, 0, 0, 0, 9]);
        }

        public FlvBuilder AudioTag() => Tag(8, 0, [0xAF, 0x01, 0x00]);

        public FlvBuilder VideoTag(int frameType, int packetType, uint timestampMs, byte[] payload) =>
            Tag(9, timestampMs, [(byte)((frameType << 4) | 7), (byte)packetType, 0, 0, 0, .. payload]);

        private FlvBuilder Tag(byte type, uint timestampMs, byte[] data)
        {
            WriteUInt32(_previousTagSize);
            _stream.WriteByte(type);
            WriteUInt24((uint)data.Length);
            WriteUInt24(timestampMs & 0xFFFFFF);
            _stream.WriteByte((byte)(timestampMs >> 24));
            WriteUInt24(0);
            _stream.Write(data);
            _previousTagSize = (uint)(11 + data.Length);
            return this;
        }

        public byte[] Build()
        {
            WriteUInt32(_previousTagSize);
            return _stream.ToArray();
        }

        private void WriteUInt32(uint value) =>
            _stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private void WriteUInt24(uint value) =>
            _stream.Write([(byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }
}
