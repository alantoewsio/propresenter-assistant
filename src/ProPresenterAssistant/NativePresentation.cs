using System.IO;
using System.Text;

namespace ProPresenterAssistant;

// Minimal protobuf writer. Field layout verified against ProPresenter 21.4.2's
// installed serialization descriptors. No Renewed Vision DLLs are distributed.
// This file format is not a public API; compatibility must be tested per version.
public sealed class Proto
{
    private readonly MemoryStream stream = new();
    private void Var(ulong value) { while (value > 127) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; } stream.WriteByte((byte)value); }
    public Proto Int(int field, ulong value) { Var((ulong)(field << 3)); Var(value); return this; }
    public Proto Double(int field, double value) { Var((ulong)((field << 3) | 1)); stream.Write(BitConverter.GetBytes(value)); return this; }
    public Proto Float(int field, float value) { Var((ulong)((field << 3) | 5)); stream.Write(BitConverter.GetBytes(value)); return this; }
    public Proto Bytes(int field, byte[] bytes) { Var((ulong)((field << 3) | 2)); Var((ulong)bytes.Length); stream.Write(bytes); return this; }
    public Proto Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());
    public byte[] ToArray() => stream.ToArray();
}

public static class NativePresentation
{
    private static Proto Uuid(string? id = null) => new Proto().Text(1, (id ?? Guid.NewGuid().ToString()).ToLowerInvariant());
    private static Proto Size(double width, double height) => new Proto().Double(1, width).Double(2, height);
    public static byte[] Create(string name, string id, IReadOnlyList<PreparedMedia> items)
    {
        string groupId = Guid.NewGuid().ToString().ToUpperInvariant();
        string arrangementId = Guid.NewGuid().ToString().ToUpperInvariant();
        var app = new Proto().Int(1, 2).Int(3, 1).Message(4, new Proto().Int(1, 7).Int(2, 0));
        var result = new Proto().Message(1, app).Message(2, Uuid(id)).Text(3, name).Text(7, "Imported by ProPresenter Assistant")
            .Message(10, Uuid(arrangementId)).Message(11, new Proto().Message(1, Uuid(arrangementId)).Text(2, "Default").Message(3, Uuid(groupId)));
        var group = new Proto().Message(1, new Proto().Message(1, Uuid(groupId)).Text(2, name)
            .Message(3, new Proto().Float(1, 0.38f).Float(2, 0.32f).Float(3, 0.95f).Float(4, 1)));
        foreach (var item in items)
        {
            string cueId = Guid.NewGuid().ToString().ToUpperInvariant();
            group.Message(2, Uuid(cueId));
            var url = new Proto().Text(1, Path.GetFullPath(item.Path)).Int(3, 2);
            var drawing = new Proto().Message(5, Size(item.Width, item.Height));
            var file = new Proto().Message(1, url);
            var media = new Proto().Message(1, Uuid()).Message(2, url)
                .Message(3, new Proto().Text(5, Path.GetExtension(item.Path).TrimStart('.')).Int(6, 1));
            if (item.Video)
                media.Message(6, new Proto().Message(1, drawing).Message(2, new Proto().Double(1, 1))
                    .Message(3, new Proto().Double(1, 1).Double(3, item.Duration).Double(11, item.Duration).Int(15, 1))
                    .Message(4, new Proto().Double(1, item.FrameRate).Int(2, 1)).Message(5, file));
            else media.Message(5, new Proto().Message(1, drawing).Message(2, file));
            var mediaType = new Proto().Message(5, media).Int(10, 1).Int(11, 1).Message(item.Video ? 7 : 6, new Proto());
            var mediaAction = new Proto().Message(1, Uuid()).Text(2, item.Name).Int(6, 1).Int(9, 2).Message(20, mediaType);
            var slide = new Proto().Message(6, Size(1920, 1080)).Message(7, Uuid());
            var slideAction = new Proto().Message(1, Uuid()).Int(6, 1).Int(9, 11)
                .Message(23, new Proto().Message(2, new Proto().Message(1, slide)));
            result.Message(13, new Proto().Message(1, Uuid(cueId)).Text(2, item.Name).Int(12, 1).Message(10, slideAction).Message(10, mediaAction));
        }
        return result.Message(12, group).ToArray();
    }
}
