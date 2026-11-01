using System.Diagnostics;

using UniversalBackupTool.Interface.Interfaces.Vortex;

namespace UniversalBackupTool.Service.Services.Vortex
{
    public sealed class VortexProcessService : IVortexProcessService
    {
        public bool IsVortexRunning()
        {
            var processes = Process.GetProcessesByName("Vortex");

            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
    }
}