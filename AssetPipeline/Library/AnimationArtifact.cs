using System.Runtime.InteropServices;

namespace BallisticEngine.AssetPipeline;

// Engine-native binary animation clip, Library\Artifacts\<guid>.banim:
//   u32 magic 'BANM' | u32 version | string name | f32 durationTicks | f32 ticksPerSecond
//   i32 channelCount
//   per channel: i32 boneIndex
//                i32 posKeyCount  | VectorKey[posKeyCount]      (f32 time, Vector3 value)
//                i32 rotKeyCount  | QuaternionKey[rotKeyCount]  (f32 time, Quaternion value)
//                i32 scaleKeyCount| VectorKey[scaleKeyCount]
// Keys are blittable structs, blitted with MemoryMarshal. One artifact per source-model animation.
public static class AnimationArtifact {
    const uint Magic = 0x4D4E4142; // "BANM"
    const uint FormatVersion = 1;

    public static void Write(string path, in AnimationClipData clip) {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(clip.Name ?? "");
        writer.Write(clip.DurationTicks);
        writer.Write(clip.TicksPerSecond);

        BoneChannel[] channels = clip.Channels ?? [];
        writer.Write(channels.Length);
        foreach (BoneChannel channel in channels) {
            writer.Write(channel.BoneIndex);
            WriteKeys(writer, channel.PositionKeys);
            WriteKeys(writer, channel.RotationKeys);
            WriteKeys(writer, channel.ScaleKeys);
        }
    }

    static void WriteKeys<T>(BinaryWriter writer, T[] keys) where T : unmanaged {
        keys ??= [];
        writer.Write(keys.Length);
        writer.Write(MemoryMarshal.AsBytes<T>(keys));
    }

    public static AnimationClipData Read(Stream stream, string sourceName = "<stream>") {
        using BinaryReader reader = new(stream);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException($"'{sourceName}' is not an animation artifact (bad magic).");
        var version = reader.ReadUInt32();
        if (version is < 1 or > FormatVersion)
            throw new InvalidDataException($"Animation artifact '{sourceName}' has unsupported version {version}.");

        var name = reader.ReadString();
        var durationTicks = reader.ReadSingle();
        var ticksPerSecond = reader.ReadSingle();

        var channelCount = reader.ReadInt32();
        var channels = new BoneChannel[channelCount];
        for (var i = 0; i < channelCount; i++) {
            var boneIndex = reader.ReadInt32();
            VectorKey[] posKeys = ReadKeys<VectorKey>(reader);
            QuaternionKey[] rotKeys = ReadKeys<QuaternionKey>(reader);
            VectorKey[] scaleKeys = ReadKeys<VectorKey>(reader);
            channels[i] = new BoneChannel(boneIndex, posKeys, rotKeys, scaleKeys);
        }

        return new AnimationClipData(name, durationTicks, ticksPerSecond, channels);
    }

    static T[] ReadKeys<T>(BinaryReader reader) where T : unmanaged {
        var count = reader.ReadInt32();
        var result = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes<T>(result));
        return result;
    }
}
