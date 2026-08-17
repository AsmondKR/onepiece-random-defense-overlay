using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrandMemoryDiagnostics;

Console.OutputEncoding = Encoding.UTF8;

var processName = ValueAfter(args, "--process") ?? "Warcraft III";
var terms = ValuesAfter(args, "--find").ToArray();
var extractRawcodes = args.Contains("--extract-rawcodes", StringComparer.OrdinalIgnoreCase);
var dumpValue = ValueAfter(args, "--dump");
var snapshotPath = ValueAfter(args, "--snapshot");
var compareBefore = ValueAfter(args, "--compare-before");
var compareAfter = ValueAfter(args, "--compare-after");
var pointerTargetsPath = ValueAfter(args, "--pointer-targets");
var pointerTargetKey = ValueAfter(args, "--target-key");
var pointerSnapshotPath = ValueAfter(args, "--pointer-snapshot");
var comparePointersBefore = ValueAfter(args, "--compare-pointers-before");
var comparePointersAfter = ValueAfter(args, "--compare-pointers-after");
var valueText = ValueAfter(args, "--value");
var valueSnapshotPath = ValueAfter(args, "--value-snapshot");
var filterValueSnapshotPath = ValueAfter(args, "--filter-value-snapshot");
var objectArrayText = ValueAfter(args, "--object-array");
var objectCountText = ValueAfter(args, "--object-count");
var rawcodeText = ValueAfter(args, "--find-rawcode");
var scanInventoryJson = args.Contains("--scan-inventory-json", StringComparer.OrdinalIgnoreCase);
var watchRangeText = ValueAfter(args, "--watch-range");
var rangeSizeText = ValueAfter(args, "--range-size");
var watchSecondsText = ValueAfter(args, "--seconds");
var findQwordText = ValueAfter(args, "--find-qword");
var findQwordMaxText = ValueAfter(args, "--find-qword-max");
var probeUnitHitsPath = ValueAfter(args, "--probe-unit-hits");

if (compareBefore is not null && compareAfter is not null)
{
    CompareSnapshots(compareBefore, compareAfter);
    return 0;
}

if (comparePointersBefore is not null && comparePointersAfter is not null)
{
    ComparePointerSnapshots(comparePointersBefore, comparePointersAfter);
    return 0;
}
if (terms.Length == 0 && dumpValue is null && pointerTargetsPath is null && valueSnapshotPath is null && objectArrayText is null && !scanInventoryJson && watchRangeText is null && findQwordText is null && probeUnitHitsPath is null)
{
    Console.Error.WriteLine("사용법: OrandMemoryDiagnostics --process \"Warcraft III\" --find \"원피스\" --find \"야마토\"");
    return 2;
}

var process = Process.GetProcessesByName(processName).OrderByDescending(x => x.StartTime).FirstOrDefault();
if (process is null)
{
    Console.Error.WriteLine($"프로세스를 찾지 못했습니다: {processName}");
    return 3;
}

var version = process.MainModule?.FileVersionInfo.FileVersion ?? "unknown";
Console.WriteLine($"대상: {process.ProcessName} PID={process.Id} Version={version}");
Console.WriteLine("접근: PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ");

using var reader = new WindowsMemoryReader(process);

if (probeUnitHitsPath is not null)
{
    if (rawcodeText is null || rawcodeText.Length != 4)
        throw new ArgumentException("--probe-unit-hits에는 4자 --find-rawcode가 필요합니다.");
    ProbeUnitHits(reader, probeUnitHitsPath, rawcodeText);
    return 0;
}

if (findQwordText is not null)
{
    var minimum = ParseAddress(findQwordText);
    var maximum = findQwordMaxText is null ? minimum : ParseAddress(findQwordMaxText);
    ScanQwordRange(reader, minimum, maximum);
    return 0;
}

if (watchRangeText is not null)
{
    var rangeSize = int.TryParse(rangeSizeText, out var parsedRange) ? Math.Clamp(parsedRange, 4096, 16 * 1024 * 1024) : 1024 * 1024;
    var seconds = int.TryParse(watchSecondsText, out var parsedSeconds) ? Math.Clamp(parsedSeconds, 1, 120) : 20;
    WatchInventoryRange(reader, ParseAddress(watchRangeText), rangeSize, seconds);
    return 0;
}

if (scanInventoryJson)
{
    ScanInventoryJson(reader);
    return 0;
}

if (objectArrayText is not null)
{
    if (!int.TryParse(objectCountText, out var objectCount) || objectCount <= 0 || rawcodeText is null || rawcodeText.Length != 4)
        throw new ArgumentException("--object-array에는 --object-count와 4자 --find-rawcode가 필요합니다.");
    ScanObjectArray(reader, ParseAddress(objectArrayText), Math.Clamp(objectCount, 1, 10000), rawcodeText);
    return 0;
}

if (valueSnapshotPath is not null)
{
    if (!int.TryParse(valueText, out var expectedValue))
        throw new ArgumentException("--value-snapshot에는 정수 --value가 필요합니다.");
    CaptureOrFilterValue(reader, expectedValue, valueSnapshotPath, filterValueSnapshotPath, version);
    return 0;
}

if (pointerTargetsPath is not null)
{
    if (pointerTargetKey is null || pointerSnapshotPath is null)
        throw new ArgumentException("--pointer-targets에는 --target-key와 --pointer-snapshot이 필요합니다.");
    ProbePointers(reader, pointerTargetsPath, pointerTargetKey, pointerSnapshotPath, version);
    return 0;
}

if (dumpValue is not null)
{
    var address = ParseAddress(dumpValue);
    var size = int.TryParse(ValueAfter(args, "--size"), out var parsedSize)
        ? Math.Clamp(parsedSize, 16, 4096)
        : 256;
    var bytes = new byte[size];
    var read = reader.Read(address, bytes, size);
    Console.WriteLine($"덤프: 0x{address:X16}, {read}바이트");
    PrintHex(bytes.AsSpan(0, read), address);
    return read > 0 ? 0 : 4;
}

var patterns = terms.SelectMany(term => new[]
{
    new Pattern(term, "UTF-8", Encoding.UTF8.GetBytes(term)),
    new Pattern(term, "UTF-16LE", Encoding.Unicode.GetBytes(term))
}).Where(x => x.Bytes.Length > 0).ToArray();

const int chunkSize = 1024 * 1024;
var overlap = Math.Max(extractRawcodes ? 768 : 1, patterns.Max(x => x.Bytes.Length) - 1);
var buffer = new byte[chunkSize + overlap];
var hits = new Dictionary<(string Term, string Encoding), List<ulong>>();
var hitCounts = new Dictionary<(string Term, string Encoding), long>();
var rawcodeCandidates = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
foreach (var pattern in patterns)
{
    hits[(pattern.Term, pattern.Encoding)] = [];
    hitCounts[(pattern.Term, pattern.Encoding)] = 0;
    rawcodeCandidates.TryAdd(pattern.Term, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

long scanned = 0;
var regionCount = 0;
foreach (var region in reader.Regions())
{
    regionCount++;
    ulong offset = 0;
    var carry = 0;
    while (offset < region.Size)
    {
        var request = (int)Math.Min((ulong)chunkSize, region.Size - offset);
        if (carry > 0) Array.Copy(buffer, chunkSize, buffer, 0, carry);
        // ReadProcessMemory requires an array starting at index zero; copy into the shared scan buffer.
        var fresh = new byte[request];
        var confirmed = reader.Read(region.Address + offset, fresh, request);
        if (confirmed <= 0) break;
        Array.Copy(fresh, 0, buffer, carry, confirmed);
        var available = carry + confirmed;

        foreach (var pattern in patterns)
        {
            var list = hits[(pattern.Term, pattern.Encoding)];
            var start = 0;
            while (start <= available - pattern.Bytes.Length)
            {
                var index = buffer.AsSpan(start, available - start).IndexOf(pattern.Bytes);
                if (index < 0) break;
                var absoluteIndex = start + index;
                var hitAddress = region.Address + offset - (ulong)carry + (ulong)absoluteIndex;
                hitCounts[(pattern.Term, pattern.Encoding)]++;
                if (snapshotPath is not null || list.Count < 32) list.Add(hitAddress);
                if (extractRawcodes && pattern.Encoding == "UTF-8")
                {
                    var contextStart = Math.Max(0, absoluteIndex - 700);
                    var context = Encoding.UTF8.GetString(buffer, contextStart, absoluteIndex - contextStart);
                    var matches = Regex.Matches(context, "유닛\\s*:\\s*([A-Za-z0-9]{4})");
                    if (matches.Count > 0)
                    {
                        var code = matches[^1].Groups[1].Value;
                        var candidates = rawcodeCandidates[pattern.Term];
                        candidates[code] = candidates.GetValueOrDefault(code) + 1;
                    }
                }
                start = absoluteIndex + 1;
            }
        }

        carry = Math.Min(overlap, available);
        Array.Copy(buffer, available - carry, buffer, chunkSize, carry);
        offset += (ulong)confirmed;
        scanned += confirmed;
    }
}

Console.WriteLine($"읽은 영역: {regionCount:N0}, 읽은 바이트: {scanned:N0}");
foreach (var pattern in patterns)
{
    var list = hits[(pattern.Term, pattern.Encoding)];
    Console.WriteLine($"[{pattern.Encoding}] {pattern.Term}: {hitCounts[(pattern.Term, pattern.Encoding)]:N0}개");
    foreach (var address in list.Take(12)) Console.WriteLine($"  0x{address:X16}");
    if (extractRawcodes && pattern.Encoding == "UTF-8")
    {
        var candidates = rawcodeCandidates[pattern.Term].OrderByDescending(x => x.Value).Take(12);
        Console.WriteLine("  rawcode 후보: " + string.Join(", ", candidates.Select(x => $"{x.Key}({x.Value})")));
    }
}

if (snapshotPath is not null)
{
    var snapshot = new AddressSnapshot
    {
        ProcessName = processName,
        ProcessId = process.Id,
        Version = version,
        CapturedAt = DateTimeOffset.Now,
        Hits = hits.ToDictionary(
            x => $"{x.Key.Term}|{x.Key.Encoding}",
            x => x.Value.Distinct().Order().ToArray(),
            StringComparer.OrdinalIgnoreCase)
    };
    var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"스냅샷 저장: {Path.GetFullPath(snapshotPath)}");
}

return 0;

static string? ValueAfter(string[] source, string key)
{
    for (var i = 0; i < source.Length - 1; i++)
        if (source[i].Equals(key, StringComparison.OrdinalIgnoreCase)) return source[i + 1];
    return null;
}

static IEnumerable<string> ValuesAfter(string[] source, string key)
{
    for (var i = 0; i < source.Length - 1; i++)
        if (source[i].Equals(key, StringComparison.OrdinalIgnoreCase)) yield return source[i + 1];
}

static ulong ParseAddress(string value)
{
    var normalized = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    return Convert.ToUInt64(normalized, 16);
}

static void PrintHex(ReadOnlySpan<byte> bytes, ulong baseAddress)
{
    for (var offset = 0; offset < bytes.Length; offset += 16)
    {
        var row = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
        var hex = string.Join(" ", row.ToArray().Select(x => x.ToString("X2"))).PadRight(47);
        var text = new string(row.ToArray().Select(x => x is >= 32 and <= 126 ? (char)x : '.').ToArray());
        Console.WriteLine($"{baseAddress + (ulong)offset:X16}  {hex}  {text}");
    }
}

static void CompareSnapshots(string beforePath, string afterPath)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var before = JsonSerializer.Deserialize<AddressSnapshot>(File.ReadAllText(beforePath), options)
        ?? throw new InvalidDataException("이전 스냅샷을 읽을 수 없습니다.");
    var after = JsonSerializer.Deserialize<AddressSnapshot>(File.ReadAllText(afterPath), options)
        ?? throw new InvalidDataException("이후 스냅샷을 읽을 수 없습니다.");
    Console.WriteLine($"비교: {before.CapturedAt:O} -> {after.CapturedAt:O}");
    foreach (var key in before.Hits.Keys.Union(after.Hits.Keys).Order())
    {
        var left = before.Hits.GetValueOrDefault(key) ?? [];
        var right = after.Hits.GetValueOrDefault(key) ?? [];
        var added = right.Except(left).ToArray();
        var removed = left.Except(right).ToArray();
        Console.WriteLine($"{key}: +{added.Length} / -{removed.Length} / 유지 {right.Intersect(left).Count()}");
        foreach (var address in added.Take(20)) Console.WriteLine($"  + 0x{address:X16}");
        foreach (var address in removed.Take(8)) Console.WriteLine($"  - 0x{address:X16}");
        var shift = FindDominantShift(removed, added);
        if (shift is not null)
        {
            var addedSet = added.ToHashSet();
            var pairedRemoved = removed.Where(x => TryShift(x, shift.Value, out var moved) && addedSet.Remove(moved)).ToArray();
            var residualRemoved = removed.Except(pairedRemoved).ToArray();
            var residualAdded = addedSet.Order().ToArray();
            Console.WriteLine($"  공통 이동 {(shift.Value >= 0 ? "+" : "-")}0x{Math.Abs(shift.Value):X} 상쇄: +{residualAdded.Length} / -{residualRemoved.Length}");
            foreach (var address in residualAdded.Take(20)) Console.WriteLine($"    +잔여 0x{address:X16}");
            foreach (var address in residualRemoved.Take(20)) Console.WriteLine($"    -잔여 0x{address:X16}");
        }
    }
}

static long? FindDominantShift(ulong[] removed, ulong[] added)
{
    if (removed.Length < 4 || added.Length < 4) return null;
    var counts = new Dictionary<long, int>();
    foreach (var left in removed)
    foreach (var right in added)
    {
        var delta = unchecked((long)right - (long)left);
        if (delta == 0 || Math.Abs(delta) > 0x10000) continue;
        counts[delta] = counts.GetValueOrDefault(delta) + 1;
    }
    if (counts.Count == 0) return null;
    var best = counts.MaxBy(x => x.Value);
    return best.Value >= Math.Min(removed.Length, added.Length) / 3 ? best.Key : null;
}

static bool TryShift(ulong value, long shift, out ulong result)
{
    if (shift >= 0)
    {
        result = value + (ulong)shift;
        return result >= value;
    }
    var amount = (ulong)(-shift);
    result = value >= amount ? value - amount : 0;
    return value >= amount;
}

static void ProbePointers(
    WindowsMemoryReader reader,
    string targetsPath,
    string targetKey,
    string outputPath,
    string version)
{
    var source = JsonSerializer.Deserialize<AddressSnapshot>(File.ReadAllText(targetsPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("주소 스냅샷을 읽을 수 없습니다.");
    if (!source.Hits.TryGetValue(targetKey, out var markers))
        throw new InvalidDataException($"스냅샷에 키가 없습니다: {targetKey}");

    var targets = new HashSet<ulong>();
    foreach (var marker in markers)
    {
        var start = (marker > 0x200 ? marker - 0x200 : 0) & ~7UL;
        var end = (marker + 0x40) & ~7UL;
        for (var candidate = start; candidate <= end; candidate += 8) targets.Add(candidate);
    }

    var references = targets.ToDictionary(x => x, _ => new List<ulong>());
    const int chunkSize = 1024 * 1024;
    long scanned = 0;
    foreach (var region in reader.Regions())
    {
        ulong offset = 0;
        while (offset < region.Size)
        {
            var request = (int)Math.Min((ulong)chunkSize, region.Size - offset);
            var bytes = new byte[request];
            var read = reader.Read(region.Address + offset, bytes, request);
            if (read <= 0) break;
            for (var index = 0; index <= read - 8; index += 8)
            {
                var value = BitConverter.ToUInt64(bytes, index);
                if (references.TryGetValue(value, out var locations) && locations.Count < 4096)
                    locations.Add(region.Address + offset + (ulong)index);
            }
            offset += (ulong)read;
            scanned += read;
        }
    }

    var result = new PointerSnapshot
    {
        ProcessId = reader.Process.Id,
        Version = version,
        CapturedAt = DateTimeOffset.Now,
        TargetKey = targetKey,
        MarkerAddresses = markers,
        References = references.Where(x => x.Value.Count > 0)
            .ToDictionary(x => x.Key, x => x.Value.Distinct().Order().ToArray())
    };
    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"포인터 후보 {targets.Count:N0}개, 참조된 후보 {result.References.Count:N0}개, 스캔 {scanned:N0}바이트");
    Console.WriteLine($"포인터 스냅샷 저장: {Path.GetFullPath(outputPath)}");
}

static void ComparePointerSnapshots(string beforePath, string afterPath)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var before = JsonSerializer.Deserialize<PointerSnapshot>(File.ReadAllText(beforePath), options)
        ?? throw new InvalidDataException("이전 포인터 스냅샷을 읽을 수 없습니다.");
    var after = JsonSerializer.Deserialize<PointerSnapshot>(File.ReadAllText(afterPath), options)
        ?? throw new InvalidDataException("이후 포인터 스냅샷을 읽을 수 없습니다.");
    Console.WriteLine($"포인터 비교: {before.CapturedAt:O} -> {after.CapturedAt:O}");
    var changes = new List<(ulong Target, ulong[] Added, ulong[] Removed, int Stable)>();
    foreach (var target in before.References.Keys.Union(after.References.Keys))
    {
        var left = before.References.GetValueOrDefault(target) ?? [];
        var right = after.References.GetValueOrDefault(target) ?? [];
        var added = right.Except(left).ToArray();
        var removed = left.Except(right).ToArray();
        if (added.Length > 0 || removed.Length > 0)
            changes.Add((target, added, removed, right.Intersect(left).Count()));
    }
    foreach (var change in changes
                 .OrderBy(x => Math.Abs(x.Added.Length - x.Removed.Length) == 1 ? 0 : 1)
                 .ThenBy(x => x.Added.Length + x.Removed.Length)
                 .Take(100))
    {
        Console.WriteLine($"target 0x{change.Target:X16}: +{change.Added.Length} / -{change.Removed.Length} / 유지 {change.Stable}");
        foreach (var location in change.Added.Take(6)) Console.WriteLine($"  +ref 0x{location:X16}");
        foreach (var location in change.Removed.Take(6)) Console.WriteLine($"  -ref 0x{location:X16}");
    }
    Console.WriteLine($"변경된 포인터 후보: {changes.Count:N0}개");
}

static void CaptureOrFilterValue(
    WindowsMemoryReader reader,
    int expectedValue,
    string outputPath,
    string? inputSnapshotPath,
    string version)
{
    ulong[] matches;
    if (inputSnapshotPath is not null)
    {
        var prior = JsonSerializer.Deserialize<ValueSnapshot>(File.ReadAllText(inputSnapshotPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("기존 값 스냅샷을 읽을 수 없습니다.");
        const ulong pageMask = ~0xFFFUL;
        var filtered = new List<ulong>();
        foreach (var page in prior.Addresses.GroupBy(address => address & pageMask))
        {
            var bytes = new byte[4096];
            var read = reader.Read(page.Key, bytes, bytes.Length);
            if (read <= 0) continue;
            foreach (var address in page)
            {
                var offset = checked((int)(address - page.Key));
                if (offset <= read - 4 && BitConverter.ToInt32(bytes, offset) == expectedValue)
                    filtered.Add(address);
            }
        }
        matches = filtered.ToArray();
        Console.WriteLine($"기존 후보 {prior.Addresses.Length:N0}개 중 값 {expectedValue} 유지 후보 {matches.Length:N0}개");
    }
    else
    {
        const uint memPrivate = 0x20000;
        const uint writableMask = 0x04 | 0x08 | 0x40 | 0x80;
        const int chunkSize = 1024 * 1024;
        var found = new List<ulong>();
        long scanned = 0;
        foreach (var region in reader.Regions().Where(x => x.Type == memPrivate && (x.Protection & writableMask) != 0))
        {
            ulong offset = 0;
            while (offset < region.Size)
            {
                var request = (int)Math.Min((ulong)chunkSize, region.Size - offset);
                var bytes = new byte[request];
                var read = reader.Read(region.Address + offset, bytes, request);
                if (read <= 0) break;
                for (var index = 0; index <= read - 4; index += 4)
                    if (BitConverter.ToInt32(bytes, index) == expectedValue)
                        found.Add(region.Address + offset + (ulong)index);
                offset += (ulong)read;
                scanned += read;
            }
        }
        matches = found.ToArray();
        Console.WriteLine($"쓰기 가능한 private 메모리 {scanned:N0}바이트에서 값 {expectedValue} 후보 {matches.Length:N0}개");
    }

    var snapshot = new ValueSnapshot
    {
        ProcessId = reader.Process.Id,
        Version = version,
        CapturedAt = DateTimeOffset.Now,
        Value = expectedValue,
        Addresses = matches
    };
    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(snapshot));
    Console.WriteLine($"값 스냅샷 저장: {Path.GetFullPath(outputPath)}");
}

static void ScanQwordRange(WindowsMemoryReader reader, ulong minimumValue, ulong maximumValue)
{
    if (maximumValue < minimumValue) (minimumValue, maximumValue) = (maximumValue, minimumValue);
    const int chunkSize = 1024 * 1024;
    var matches = new List<(ulong Address, ulong Value)>();
    long scanned = 0;
    foreach (var region in reader.Regions())
    {
        ulong offset = 0;
        while (offset < region.Size)
        {
            var request = (int)Math.Min((ulong)chunkSize, region.Size - offset);
            var bytes = new byte[request];
            var read = reader.Read(region.Address + offset, bytes, request);
            if (read <= 0) break;
            for (var index = 0; index <= read - 8; index += 8)
            {
                var value = BitConverter.ToUInt64(bytes, index);
                if (value >= minimumValue && value <= maximumValue)
                    matches.Add((region.Address + offset + (ulong)index, value));
            }
            offset += (ulong)read;
            scanned += read;
        }
    }

    Console.WriteLine($"QWORD 0x{minimumValue:X16}..0x{maximumValue:X16}: {matches.Count:N0}개, 스캔 {scanned:N0}바이트");
    foreach (var match in matches.Take(1024)) Console.WriteLine($"  at=0x{match.Address:X16} value=0x{match.Value:X16}");
}

static void ProbeUnitHits(WindowsMemoryReader reader, string snapshotPath, string rawcode)
{
    var source = JsonSerializer.Deserialize<AddressSnapshot>(File.ReadAllText(snapshotPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("주소 스냅샷을 읽을 수 없습니다.");
    if (!source.Hits.TryGetValue($"{rawcode}|UTF-8", out var markers))
        throw new InvalidDataException($"스냅샷에 rawcode가 없습니다: {rawcode}");

    const ulong rawcodeOffset = 0x178;
    const int ownerOffset = 0x1C0;
    var candidates = new List<(ulong Base, ulong Vtable, byte Owner, ulong Field18, ulong Field20)>();
    foreach (var marker in markers)
    {
        if (marker < rawcodeOffset || (marker & 7) != 0) continue;
        var unitBase = marker - rawcodeOffset;
        var bytes = new byte[ownerOffset + 1];
        if (reader.Read(unitBase, bytes, bytes.Length) != bytes.Length) continue;
        if (!bytes.AsSpan((int)rawcodeOffset, 4).SequenceEqual(Encoding.ASCII.GetBytes(rawcode))) continue;
        var owner = bytes[ownerOffset];
        if (owner > 23) continue;
        var vtable = BitConverter.ToUInt64(bytes, 0);
        var field18 = BitConverter.ToUInt64(bytes, 0x18);
        var field20 = BitConverter.ToUInt64(bytes, 0x20);
        candidates.Add((unitBase, vtable, owner, field18, field20));
    }

    Console.WriteLine($"rawcode {rawcode}, +0x178 / owner +0x1C0 후보: {candidates.Count:N0}개");
    foreach (var group in candidates.GroupBy(x => x.Owner).OrderBy(x => x.Key))
        Console.WriteLine($"  owner {group.Key}: {group.Count():N0}개");
    foreach (var candidate in candidates.Take(512))
        Console.WriteLine($"  base=0x{candidate.Base:X16} vtbl=0x{candidate.Vtable:X16} owner={candidate.Owner} +18=0x{candidate.Field18:X16} +20=0x{candidate.Field20:X16}");
}

static void ScanObjectArray(WindowsMemoryReader reader, ulong arrayAddress, int count, string rawcode)
{
    var arrayBytes = new byte[count * 8];
    var arrayRead = reader.Read(arrayAddress, arrayBytes, arrayBytes.Length);
    if (arrayRead < 8) throw new InvalidOperationException("객체 포인터 배열을 읽지 못했습니다.");
    var patterns = new[]
    {
        (Name: "ascii", Bytes: Encoding.ASCII.GetBytes(rawcode)),
        (Name: "reversed", Bytes: Encoding.ASCII.GetBytes(new string(rawcode.Reverse().ToArray())))
    };
    var hits = 0;
    var scannedTargets = new HashSet<ulong>();
    for (var index = 0; index < Math.Min(count, arrayRead / 8); index++)
    {
        var address = BitConverter.ToUInt64(arrayBytes, index * 8);
        if (address == 0) continue;
        var bytes = new byte[0x400];
        var read = reader.Read(address, bytes, bytes.Length);
        if (read <= 0) continue;
        foreach (var pattern in patterns)
        {
            var offset = bytes.AsSpan(0, read).IndexOf(pattern.Bytes);
            if (offset < 0) continue;
            Console.WriteLine($"[{index}] object=0x{address:X16} {pattern.Name} offset=0x{offset:X} raw=0x{address + (ulong)offset:X16}");
            hits++;
        }
        for (var pointerOffset = 0; pointerOffset <= read - 8; pointerOffset += 8)
        {
            var target = BitConverter.ToUInt64(bytes, pointerOffset);
            if (target < 0x10000000000 || target > 0x7FFFFFFFFFFF || !scannedTargets.Add(target)) continue;
            var targetBytes = new byte[0x400];
            var targetRead = reader.Read(target, targetBytes, targetBytes.Length);
            if (targetRead <= 0) continue;
            foreach (var pattern in patterns)
            {
                var targetOffset = targetBytes.AsSpan(0, targetRead).IndexOf(pattern.Bytes);
                if (targetOffset < 0) continue;
                Console.WriteLine($"[{index}] object=0x{address:X16} ptr+0x{pointerOffset:X}->0x{target:X16} {pattern.Name} offset=0x{targetOffset:X}");
                hits++;
            }
        }
    }
    Console.WriteLine($"객체 {count:N0}개와 1단계 포인터 {scannedTargets.Count:N0}개 검사, rawcode {rawcode} 포함 {hits:N0}개");
}

static void ScanInventoryJson(WindowsMemoryReader reader)
{
    const int chunkSize = 1024 * 1024;
    var regex = new Regex("\\\"(?<id>[A-Za-z0-9]{4})\\\"\\s*:\\s*(?<count>[0-9]{1,4})", RegexOptions.Compiled);
    var clusters = new List<(ulong Address, ulong Region, ulong RegionSize, List<(string Id, int Count, int Offset)> Values, string Context)>();
    foreach (var region in reader.Regions())
    {
        ulong offset = 0;
        while (offset < region.Size)
        {
            var request = (int)Math.Min((ulong)chunkSize, region.Size - offset);
            var bytes = new byte[request];
            var read = reader.Read(region.Address + offset, bytes, request);
            if (read <= 0) break;
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            var matches = regex.Matches(text).Select(m =>
                (Id: m.Groups["id"].Value, Count: int.Parse(m.Groups["count"].Value), Offset: m.Index)).ToList();
            for (var index = 0; index < matches.Count;)
            {
                var values = new List<(string Id, int Count, int Offset)> { matches[index] };
                var cursor = index + 1;
                while (cursor < matches.Count && matches[cursor].Offset - values[^1].Offset <= 80)
                {
                    values.Add(matches[cursor]);
                    cursor++;
                }
                if (values.Count >= 3)
                {
                    var contextStart = Math.Max(0, values[0].Offset - 160);
                    var contextEnd = Math.Min(text.Length, values[^1].Offset + 240);
                    clusters.Add((region.Address + offset + (ulong)values[0].Offset, region.Address, region.Size, values,
                        text[contextStart..contextEnd].Replace("\0", "·")));
                }
                index = cursor;
            }
            offset += (ulong)read;
        }
    }
    foreach (var cluster in clusters.OrderByDescending(x => x.Values.Count).Take(20))
    {
        Console.WriteLine($"0x{cluster.Address:X16} region=0x{cluster.Region:X16}+0x{cluster.RegionSize:X} 연속 {cluster.Values.Count}개: " +
                          string.Join(", ", cluster.Values.Select(x => $"{x.Id}={x.Count}")));
        Console.WriteLine("  문맥: " + cluster.Context.Replace("\r", " ").Replace("\n", " "));
    }
    Console.WriteLine($"후보 클러스터: {clusters.Count:N0}개");
}

static void WatchInventoryRange(WindowsMemoryReader reader, ulong address, int size, int seconds)
{
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    var iterations = 0;
    var bestPairs = 0;
    var regex = new Regex("\\\"(?<id>[A-Za-z0-9]{4})\\\"\\s*:\\s*(?<count>[0-9]{1,4})", RegexOptions.Compiled);
    while (DateTime.UtcNow < deadline)
    {
        var bytes = new byte[size];
        var read = reader.Read(address, bytes, size);
        iterations++;
        if (read > 0)
        {
            var text = Encoding.UTF8.GetString(bytes, 0, read);
            var cursor = 0;
            while ((cursor = text.IndexOf("{\"units\"", cursor, StringComparison.Ordinal)) >= 0)
            {
                var end = BalancedJsonEnd(text, cursor);
                if (end > cursor)
                {
                    var json = text[cursor..(end + 1)];
                    var matches = regex.Matches(json);
                    if (matches.Count > bestPairs || json.Contains("\"300h\"", StringComparison.Ordinal))
                    {
                        bestPairs = Math.Max(bestPairs, matches.Count);
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} 0x{address + (ulong)cursor:X16} JSON {json.Length}바이트 {matches.Count}쌍: {json}");
                        if (json.Contains("\"300h\"", StringComparison.Ordinal))
                        {
                            Console.WriteLine("현재 루피 포함 완전 JSON 포착");
                            return;
                        }
                    }
                }
                cursor++;
            }
        }
        Thread.Sleep(15);
    }
    Console.WriteLine($"감시 종료: {iterations:N0}회, 최다 {bestPairs:N0}쌍");
}

static int BalancedJsonEnd(string text, int start)
{
    var depth = 0;
    var quoted = false;
    var escaped = false;
    for (var index = start; index < text.Length; index++)
    {
        var value = text[index];
        if (quoted)
        {
            if (escaped) escaped = false;
            else if (value == '\\') escaped = true;
            else if (value == '"') quoted = false;
            continue;
        }
        if (value == '"') quoted = true;
        else if (value == '{') depth++;
        else if (value == '}' && --depth == 0) return index;
    }
    return -1;
}

internal sealed record Pattern(string Term, string Encoding, byte[] Bytes);

internal sealed class AddressSnapshot
{
    public string ProcessName { get; init; } = "";
    public int ProcessId { get; init; }
    public string Version { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public Dictionary<string, ulong[]> Hits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PointerSnapshot
{
    public int ProcessId { get; init; }
    public string Version { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string TargetKey { get; init; } = "";
    public ulong[] MarkerAddresses { get; init; } = [];
    public Dictionary<ulong, ulong[]> References { get; init; } = [];
}

internal sealed class ValueSnapshot
{
    public int ProcessId { get; init; }
    public string Version { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public int Value { get; init; }
    public ulong[] Addresses { get; init; } = [];
}
