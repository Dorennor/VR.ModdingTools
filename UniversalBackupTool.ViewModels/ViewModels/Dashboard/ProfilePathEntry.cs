namespace UniversalBackupTool.ViewModel.ViewModels.Dashboard
{
    public sealed record ProfilePathEntry(Guid ProfileId, string GameName, ProfilePathKind Kind, string Path)
    {
        public string KindLabel => Kind switch
        {
            ProfilePathKind.VortexStaging => "Vortex staging",
            ProfilePathKind.GameRoot => "Game root",
            _ => "Custom path"
        };

        public string Display => $"{GameName} - {KindLabel}: {Path}";
    }
}