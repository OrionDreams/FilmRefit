using System.Collections.ObjectModel;

namespace FilmRefit.App.ViewModels;

public sealed class DirectoryGroupViewModel
{
    public DirectoryGroupViewModel(string directory, IEnumerable<VideoClipViewModel> clips)
    {
        Directory = directory;
        Clips = new ObservableCollection<VideoClipViewModel>(clips);
    }

    public string Directory { get; }

    public ObservableCollection<VideoClipViewModel> Clips { get; }
}
