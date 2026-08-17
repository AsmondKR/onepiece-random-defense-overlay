using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrandMemoryDiagnostics;

internal sealed class WindowsMemoryReader : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private readonly IntPtr _handle;

    public Process Process { get; }

    public WindowsMemoryReader(Process process)
    {
        Process = process;
        _handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, process.Id);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "대상 프로세스를 읽기 전용으로 열 수 없습니다.");
    }

    public IEnumerable<MemoryRegion> Regions()
    {
        ulong address = 0x10000;
        const ulong maxUserAddress = 0x00007FFFFFFFFFFF;
        while (address < maxUserAddress)
        {
            var queried = VirtualQueryEx(_handle, new IntPtr(unchecked((long)address)), out var info,
                (nuint)Marshal.SizeOf<MemoryBasicInformation64>());
            if (queried == 0) yield break;

            var size = info.RegionSize;
            if (size == 0) yield break;
            if (info.State == MemCommit &&
                (info.Protect & PageNoAccess) == 0 &&
                (info.Protect & PageGuard) == 0)
            {
                yield return new MemoryRegion(info.BaseAddress, size, info.Protect, info.Type);
            }

            var next = info.BaseAddress + size;
            if (next <= address) yield break;
            address = next;
        }
    }

    public int Read(ulong address, byte[] buffer, int count)
    {
        if (!ReadProcessMemory(_handle, new IntPtr(unchecked((long)address)), buffer, count, out var read))
            return 0;
        return checked((int)read);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out nuint bytesRead);

    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MemoryBasicInformation64 buffer,
        nuint length);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

internal readonly record struct MemoryRegion(ulong Address, ulong Size, uint Protection, uint Type);
