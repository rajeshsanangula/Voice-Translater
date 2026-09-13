using VTTranslate.Core.Session;

namespace VTTranslate.Core.Tests;

public class DirectionLabelTests
{
    [Theory]
    [InlineData(SessionDirection.EnglishMicToGerman, "EN→DE")]
    [InlineData(SessionDirection.GermanRemoteToEnglish, "DE→EN")]
    public void For_MapsEachDirectionToItsOwnDistinctLabel(SessionDirection direction, string expected)
    {
        Assert.Equal(expected, DirectionLabel.For(direction));
    }

    [Fact]
    public void For_ProducesDifferentLabels_ForTheTwoDirections()
    {
        // The defect this class fixes: error messages had no label at all, so it was
        // impossible to tell which direction an error came from. The core guarantee
        // this labeling must provide is that the two directions are never confusable.
        var enToDe = DirectionLabel.For(SessionDirection.EnglishMicToGerman);
        var deToEn = DirectionLabel.For(SessionDirection.GermanRemoteToEnglish);

        Assert.NotEqual(enToDe, deToEn);
    }

    [Fact]
    public void Format_PrefixesMessageWithOnlyItsOwnDirectionLabel_NeverTheOther()
    {
        const string message = "Transient error: WebSocket operation failed.";

        var enToDeFormatted = DirectionLabel.Format(SessionDirection.EnglishMicToGerman, message);
        var deToEnFormatted = DirectionLabel.Format(SessionDirection.GermanRemoteToEnglish, message);

        Assert.StartsWith("[EN→DE]", enToDeFormatted);
        Assert.DoesNotContain("[DE→EN]", enToDeFormatted);

        Assert.StartsWith("[DE→EN]", deToEnFormatted);
        Assert.DoesNotContain("[EN→DE]", deToEnFormatted);

        Assert.NotEqual(enToDeFormatted, deToEnFormatted);
    }

    [Fact]
    public void Format_PreservesTheOriginalMessage_Unmodified()
    {
        const string message = "some diagnostic detail";
        var formatted = DirectionLabel.Format(SessionDirection.EnglishMicToGerman, message);

        Assert.Contains(message, formatted);
    }
}
