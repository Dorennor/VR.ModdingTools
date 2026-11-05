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
        GameChanges,

        /// <summary>
        /// Everything added to the game folder: new files plus linked entries (Vortex deployments, also over vanilla
        /// files) with their real content. Never reads file contents, so it stays fast after a reinstall.
        /// </summary>
        GameAdditions,

        /// <summary>
        /// The whole game folder as one plain zip in the profile's export folder, with links stored as real files. It has
        /// no manifest and no retention: each run replaces the previous archive, and it is unpacked by hand.
        /// </summary>
        FullGame
    }
}