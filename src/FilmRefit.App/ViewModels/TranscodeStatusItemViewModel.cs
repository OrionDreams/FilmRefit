using FilmRefit.App.Services;

namespace FilmRefit.App.ViewModels;

public sealed class TranscodeStatusItemViewModel
{
    public TranscodeStatusItemViewModel(TranscodeMode mode, string fileName, bool succeeded, string errorText)
        : this(mode == TranscodeMode.Proxy ? "Proxy" : "Mezzanine", fileName, succeeded, errorText)
    {
    }

    public TranscodeStatusItemViewModel(string operationText, string fileName, bool succeeded, string errorText)
    {
        OperationText = operationText;
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
