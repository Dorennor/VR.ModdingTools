using System.ComponentModel;

namespace VR.ModdingTools.VortexBackup.Models
{
    /// <summary>
    /// Represents a folder that can be selected for backup
    /// </summary>
    public class FolderItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _fullPath = string.Empty;
        private bool _isSelected;
        private bool _isCustom;

        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string FullPath
        {
            get => _fullPath;
            set
            {
                if (_fullPath != value)
                {
                    _fullPath = value;
                    OnPropertyChanged(nameof(FullPath));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public bool IsCustom
        {
            get => _isCustom;
            set
            {
                if (_isCustom != value)
                {
                    _isCustom = value;
                    OnPropertyChanged(nameof(IsCustom));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}