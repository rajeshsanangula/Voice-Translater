namespace VTTranslate.Core.Session;

/// <summary>
/// Single source of truth for turning a <see cref="SessionDirection"/> into the
/// short label used in both status and error messages (e.g. "EN→DE"). Extracted as
/// its own pure, testable class because the two call sites that need this
/// (status messages and error messages) previously duplicated the mapping — and one
/// of them (error messages) was missing it entirely, making it impossible to tell
/// from the UI which recognizer direction an error belonged to.
/// </summary>
public static class DirectionLabel
{
    public static string For(SessionDirection direction) => direction switch
    {
        SessionDirection.EnglishMicToGerman => "EN→DE",
        SessionDirection.GermanRemoteToEnglish => "DE→EN",
        _ => direction.ToString()
    };

    public static string Format(SessionDirection direction, string message) => $"[{For(direction)}] {message}";
}
