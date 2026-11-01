namespace UniversalBackupTool.Interface.Interfaces.Vortex
{
    public interface IVortexProcessService
    {
        /// <summary>
        /// Whether Vortex is running. It keeps its state database in the AppData folder open and writes to it, so that
        /// folder is backed up and restored only after the user confirms.
        /// </summary>
        bool IsVortexRunning();
    }
}