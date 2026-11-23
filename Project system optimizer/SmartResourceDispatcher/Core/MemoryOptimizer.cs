using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartResourceDispatcher.Core
{
    public class MemoryOptimizer
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        public static int TrimBackgroundProcesses(int? excludeProcessId = null)
        {
            int trimmedCount = 0;
            int currentPid = Environment.ProcessId;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.HasExited || proc.Id <= 4 || proc.Id == currentPid)
                        continue;

                    if (excludeProcessId.HasValue && proc.Id == excludeProcessId.Value)
                        continue;

                    if (EmptyWorkingSet(proc.Handle) != 0)
                    {
                        trimmedCount++;
                    }
                }
                catch
                {
                    // Системные процессы или процессы без доступа пропускаем
                }
                finally
                {
                    proc.Dispose();
                }
            }

            return trimmedCount;
        }

        public static bool SetProcessPriority(string processName, ProcessPriorityClass priority)
        {
            bool anyChanged = false;
            string cleanName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            foreach (var proc in Process.GetProcessesByName(cleanName))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.PriorityClass = priority;
                        anyChanged = true;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            return anyChanged;
        }
    }
}