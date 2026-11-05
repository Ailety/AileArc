using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Used only by the measurement script. The worker inherits this outer job;
// its own nested safety job and its normal limits remain in force.
public sealed class ClientMeasurementJob : IDisposable
{
    private readonly SafeFileHandle job;
    public readonly HashSet<string> SampledProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public ClientMeasurementJob()
    {
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception(); job.Dispose(); throw error;
        }
    }
    public void Attach(Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception();
    }
    public ulong PeakCommit()
    {
        ExtendedLimits limits;
        if (!QueryInformationJobObject(job, 9, out limits, Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero)) throw new Win32Exception();
        return limits.PeakJobMemory.ToUInt64();
    }
    public uint TotalProcesses()
    {
        // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION: four time fields followed
        // by PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses.
        var data = new byte[48];
        var wait = Stopwatch.StartNew();
        do
        {
            if (!QueryInformationJobObject(job, 1, data, data.Length, IntPtr.Zero)) throw new Win32Exception();
            if (BitConverter.ToUInt32(data, 40) == 0) return BitConverter.ToUInt32(data, 36);
            System.Threading.Thread.Sleep(10);
        } while (wait.ElapsedMilliseconds < 2000);
        throw new InvalidOperationException("A measured process is still active after the client exited.");
    }
    public long SampleWorkingSet()
    {
        // The client and its single worker fit comfortably in this list. Fail
        // explicitly if the topology changes rather than reporting a partial sum.
        var pids = new byte[8 + 16 * IntPtr.Size];
        if (!QueryInformationJobObject(job, 3, pids, pids.Length, IntPtr.Zero)) throw new Win32Exception();
        int count = BitConverter.ToInt32(pids, 4);
        if (count != BitConverter.ToInt32(pids, 0)) throw new InvalidOperationException("Incomplete job process list.");
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            int id = BitConverter.ToInt32(pids, 8 + i * IntPtr.Size);
            try
            {
                using (var process = Process.GetProcessById(id))
                {
                    bool belongs;
                    if (IsProcessInJob(process.Handle, job, out belongs) && belongs)
                    {
                        SampledProcessNames.Add(process.ProcessName);
                        sum += process.WorkingSet64;
                    }
                }
            }
            catch (ArgumentException) { } // The worker exited during the sample.
            catch (InvalidOperationException) { }
        }
        return sum;
    }
    public void Dispose() { job.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public Io Counters;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out ExtendedLimits info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, [Out] byte[] info, int size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool belongs);
}
