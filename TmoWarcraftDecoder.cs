using System.Numerics;

namespace OrandOverlay;

/// <summary>
/// Pure arithmetic used by the verified TMO.GG 1.0.0.0 / Warcraft III 2.0.4.23745 bridge.
/// This type never opens, writes to, injects into, or calls code in either process.
/// </summary>
public static class TmoWarcraftDecoder
{
    public const ulong GameUiSeed1 = 0x1917B0D05C55899DUL;
    public const ulong GameUiSeed2 = 0x2694AF13DBAC0643UL;
    public const ulong WorldFrameSeed1 = 0x6502F98E2F7FD1E7UL;
    public const ulong WorldFrameSeed2 = 0x550070769B75D864UL;

    private static readonly byte?[] GeneratorSignature =
    [
        0x48, 0x89, 0x7C, 0x24, 0x18, 0x48, 0x8B, 0xFA, 0x48, 0x8B, 0xD1,
        0x0F, 0x1F, 0x44, 0x00, 0x00, 0x90, 0x80, 0xE7, 0xFF, 0x8A, 0xD2,
        0x48, 0xB9, null, null, null, null, null, null, null, null,
        0x0F, 0x1F, 0x80, 0x00, 0x00, 0x00, 0x00, 0x90, 0x80, 0xE2, 0xFF, 0x86, 0xED,
        0x48, 0x8B, 0x41, 0x30, 0x49, 0xB8, null, null, null, null, null, null, null, null,
        0x48, 0x33, 0x02, 0x49, 0x33, 0xC0, 0x48, 0x89, 0x02
    ];

    public static bool TryParseGeneratedDecoder(ReadOnlySpan<byte> code, out GeneratedDecoderMetadata metadata)
    {
        metadata = default;
        if (code.Length < GeneratorSignature.Length) return false;
        for (var index = 0; index < GeneratorSignature.Length; index++)
            if (GeneratorSignature[index] is byte expected && code[index] != expected)
                return false;

        var stateAddress = BitConverter.ToUInt64(code.Slice(24, 8));
        var xorMask = BitConverter.ToUInt64(code.Slice(51, 8));
        if (!IsPlausibleUserAddress(stateAddress) || xorMask == 0) return false;
        metadata = new GeneratedDecoderMetadata(stateAddress, xorMask);
        return true;
    }

    public static DecoderKeys DeriveKeys(ulong seed1, ulong seed2, ulong stateValue, ulong xorMask) =>
        new(seed1 ^ xorMask, seed2 ^ stateValue ^ xorMask);

    public static ulong DecodeGameUi(Func<ulong, ulong> readUInt64, ulong moduleBase, DecoderKeys keys)
    {
        var table = checked(moduleBase + 0x2B58E00UL);
        var encrypted = readUInt64(checked(moduleBase + 0x2BDAB70UL));
        var low = (uint)encrypted;
        var stage = ((ulong)((uint)(encrypted >> 32) ^ unchecked(0xF6EB0190u - low)) << 32) | low;

        var tableHigh = readUInt64(checked(table + (keys.Key1 >> 52))) >> 32;
        var high = (uint)(stage >> 32) ^ (uint)tableHigh ^ (uint)stage;
        stage = ((ulong)high << 32) | (uint)stage;
        stage ^= keys.Key2;

        var rotated = BitOperations.RotateLeft((uint)readUInt64(checked(table + (keys.Key1 & 0xFFF))), 9);
        var transformed = unchecked((uint)(2 * (uint)stage) - rotated);
        stage = ((ulong)((uint)(stage >> 32) ^ transformed) << 32) | (uint)stage;

        var last = unchecked((uint)stage + 0xD96B50EDu);
        return ((ulong)((uint)(stage >> 32) ^ last) << 32) | (uint)stage;
    }

    public static ulong DecodeWorldFrame(Func<ulong, ulong> readUInt64, ulong moduleBase, ulong gameUi,
        DecoderKeys keys)
    {
        var table = checked(moduleBase + 0x2B58E00UL);
        const ulong highMask = 0xFFFF_FFFF_0000_0000UL;
        var encrypted = readUInt64(checked(gameUi + 0x668UL));

        var low = (uint)encrypted;
        var firstTable = BitOperations.RotateRight((uint)readUInt64(checked(table + (keys.Key1 & 0xFFF))), 11);
        var stageHigh = unchecked(~low) ^ firstTable;
        var stage = ((ulong)stageHigh << 32) ^ (encrypted & highMask) | low;

        var doubled = unchecked((uint)(2 * (uint)stage + 0xFF1F1356u));
        stage = ((ulong)doubled << 32) ^ (stage & highMask) | (uint)stage;
        stage ^= keys.Key2;

        var secondTable = BitOperations.RotateRight((uint)readUInt64(checked(table + (keys.Key1 >> 52))), 11);
        var secondHigh = unchecked(~(uint)stage) ^ secondTable;
        stage = ((ulong)secondHigh << 32) ^ (stage & highMask) | (uint)stage;

        var thirdRead = readUInt64(checked(table + (keys.Key1 & 0xFFF)));
        var thirdTable = BitOperations.RotateRight((uint)(thirdRead >> 32), 11);
        var finalHigh = unchecked(~(uint)stage) ^ thirdTable;
        return ((ulong)finalHigh << 32) ^ (stage & highMask) | (uint)stage;
    }

    public static bool IsPlausibleUserAddress(ulong address) =>
        address is >= 0x10000 and <= 0x00007FFFFFFFFFFF;
}

public readonly record struct GeneratedDecoderMetadata(ulong StateAddress, ulong XorMask);
public readonly record struct DecoderKeys(ulong Key1, ulong Key2);

public static class TmoInventoryHandle
{
    public static bool IsEmpty(ulong handle) => handle is 0 or ulong.MaxValue;
}

public static class TmoHandleResolver
{
    public static bool TryResolve(Func<ulong, ulong> readUInt64, Func<ulong, uint> readUInt32,
        ulong tableRoot, ulong handle, out ulong resolved)
    {
        resolved = 0;
        if (TmoInventoryHandle.IsEmpty(handle) || !TmoWarcraftDecoder.IsPlausibleUserAddress(tableRoot))
            return false;
        var rawIndex = (uint)handle;
        var generation = (uint)(handle >> 32);
        var highTable = (rawIndex & 0x8000_0000U) != 0;
        var index = highTable ? rawIndex & 0x7FFF_FFFFU : rawIndex;
        var count = readUInt32(checked(tableRoot + (highTable ? 0x68UL : 0x30UL)));
        var entries = readUInt64(checked(tableRoot + (highTable ? 0x50UL : 0x18UL)));
        if (index >= count || count > 0x1000000 || !TmoWarcraftDecoder.IsPlausibleUserAddress(entries))
            return false;
        var slot = checked(entries + 16UL * index);
        if (readUInt32(slot) != 0xFFFF_FFFEU) return false;
        var candidate = readUInt64(checked(slot + 8));
        if (!TmoWarcraftDecoder.IsPlausibleUserAddress(candidate) ||
            readUInt32(checked(candidate + 0x24)) != generation)
            return false;
        resolved = candidate;
        return true;
    }
}

public enum GreenBloodProbeState
{
    Unknown,
    Absent,
    Held
}

public static class TmoGreenBloodProbe
{
    public const uint ControllerBaselineAbility = 0x41304B38; // canonical A0K8
    public const uint HeldAbility = 0x41313341; // canonical A13A

    public static GreenBloodProbeState Evaluate(IReadOnlyDictionary<uint, uint> abilityLevels)
    {
        if (!abilityLevels.TryGetValue(ControllerBaselineAbility, out var baselineLevel) || baselineLevel == 0)
            return GreenBloodProbeState.Unknown;
        return abilityLevels.TryGetValue(HeldAbility, out var heldLevel) && heldLevel > 0
            ? GreenBloodProbeState.Held
            : GreenBloodProbeState.Absent;
    }

    public static GreenBloodProbeState Combine(GreenBloodProbeState first, GreenBloodProbeState second) =>
        first != GreenBloodProbeState.Unknown && first == second ? first : GreenBloodProbeState.Unknown;
}
