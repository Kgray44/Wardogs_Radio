using System.Buffers.Binary;

namespace WardogsRadio.Input;

public sealed record HidControlCapabilities(int Buttons, int Axes, int Povs, int ButtonGroups, int ValueGroups)
{
    public bool HasGameControls => Buttons > 0 || Axes > 0 || Povs > 0;
}

/// <summary>Interprets the fixed-size Windows HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS records.</summary>
public static class HidCapabilityClassifier
{
    public const int CapabilityRecordBytes = 72;

    public static HidControlCapabilities Parse(ReadOnlySpan<byte> buttonCaps, int buttonGroups,
        ReadOnlySpan<byte> valueCaps, int valueGroups)
    {
        var buttons = new HashSet<int>();
        var axes = new HashSet<int>();
        var povs = new HashSet<int>();
        for (var index = 0; index < buttonGroups && (index + 1) * CapabilityRecordBytes <= buttonCaps.Length; index++)
        {
            var cap = buttonCaps.Slice(index * CapabilityRecordBytes, CapabilityRecordBytes);
            if (BinaryPrimitives.ReadUInt16LittleEndian(cap) != 0x09 || cap[3] != 0) continue;
            foreach (var usage in Usages(cap)) if (usage > 0) buttons.Add(usage);
        }
        for (var index = 0; index < valueGroups && (index + 1) * CapabilityRecordBytes <= valueCaps.Length; index++)
        {
            var cap = valueCaps.Slice(index * CapabilityRecordBytes, CapabilityRecordBytes);
            if (BinaryPrimitives.ReadUInt16LittleEndian(cap) != 0x01 || cap[3] != 0) continue;
            foreach (var usage in Usages(cap))
            {
                if (usage is >= 0x30 and <= 0x38) axes.Add(usage);
                else if (usage == 0x39) povs.Add(usage);
            }
        }
        return new(buttons.Count, axes.Count, povs.Count, buttonGroups, valueGroups);
    }

    static IEnumerable<int> Usages(ReadOnlySpan<byte> cap)
    {
        // Iterators cannot capture a ReadOnlySpan; materialize at most 512 values.
        var minimum = BinaryPrimitives.ReadUInt16LittleEndian(cap.Slice(56, 2));
        if (cap[12] == 0) return minimum == 0 ? [] : [minimum];
        var maximum = BinaryPrimitives.ReadUInt16LittleEndian(cap.Slice(58, 2));
        return maximum < minimum || maximum - minimum > 512 ? [] : Enumerable.Range(minimum, maximum - minimum + 1);
    }
}
