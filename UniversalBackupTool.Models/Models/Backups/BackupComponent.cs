namespace UniversalBackupTool.Model.Models.Backups
{
    public enum BackupComponent
    {
        StagingFolders,

        /// <summary>
        /// Files in the game folder that are not part of the baseline snapshot (new files only).
        /// </summary>
        GameConfigs,

        VortexAppData,
        CustomFolders,

        /// <summary>
        /// Every file in the game folder that differs from the baseline snapshot: new and modified files.
        /// </summary>
        GameChanges
    }
}