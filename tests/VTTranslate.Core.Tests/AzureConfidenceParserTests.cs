using VTTranslate.Core.Providers;

namespace VTTranslate.Core.Tests;

public class AzureConfidenceParserTests
{
    [Fact]
    public void TryParse_ValidNBestJson_ReturnsConfidence()
    {
        const string json = """{"NBest":[{"Confidence":0.87,"Lexical":"hello world"}]}""";

        var result = AzureConfidenceParser.TryParse(json);

        Assert.Equal(0.87, result);
    }

    [Fact]
    public void TryParse_UsesFirstNBestEntryOnly()
    {
        const string json = """{"NBest":[{"Confidence":0.91},{"Confidence":0.2}]}""";

        var result = AzureConfidenceParser.TryParse(json);

        Assert.Equal(0.91, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_NullOrBlankInput_ReturnsNull(string? input)
    {
        Assert.Null(AzureConfidenceParser.TryParse(input));
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull_NeverThrows()
    {
        var exception = Record.Exception(() => AzureConfidenceParser.TryParse("{not valid json"));

        Assert.Null(exception);
        Assert.Null(AzureConfidenceParser.TryParse("{not valid json"));
    }

    [Fact]
    public void TryParse_MissingNBestArray_ReturnsNull_NeverInventsAValue()
    {
        const string json = """{"DisplayText":"hello"}""";

        Assert.Null(AzureConfidenceParser.TryParse(json));
    }

    [Fact]
    public void TryParse_EmptyNBestArray_ReturnsNull()
    {
        const string json = """{"NBest":[]}""";

        Assert.Null(AzureConfidenceParser.TryParse(json));
    }

    [Fact]
    public void TryParse_NBestEntryMissingConfidenceField_ReturnsNull()
    {
        const string json = """{"NBest":[{"Lexical":"hello"}]}""";

        Assert.Null(AzureConfidenceParser.TryParse(json));
    }

    [Fact]
    public void TryParse_ConfidenceIsNotANumber_ReturnsNull_NeverGuesses()
    {
        const string json = """{"NBest":[{"Confidence":"high"}]}""";

        Assert.Null(AzureConfidenceParser.TryParse(json));
    }
}
