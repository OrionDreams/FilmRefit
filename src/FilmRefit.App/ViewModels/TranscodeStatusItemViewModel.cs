using FilmRefit.App.Services;

namespace FilmRefit.App.ViewModels;

public sealed class TranscodeStatusItemViewModel
{
    public TranscodeStatusItemViewModel(TranscodeMode mode, string fileName, bool succeeded, string errorText)
    {
        OperationText = mode == TranscodeMode.Proxy ? "Proxy" : "Mezzanine";
        FileName = fileName;
        Succeeded = succeeded;
        ErrorText = errorText;
    }

    public string OperationText { get; }

    public string FileName { get; }

    public bool Succeeded { get; }

    public bool Failed => !Succeeded;

    public string ErrorText { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
}
