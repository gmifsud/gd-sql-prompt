using SqlCompanion.Engine.Binding;

namespace SqlCompanion.Engine.Tests;

public sealed class CompletionTests
{
    private readonly KeywordCompletionService _service = new();

    /// <summary>The caret is marked with '|' in <paramref name="markedText"/>.</summary>
    [Theory]
    [InlineData("SELECT 'a|bc'")]
    [InlineData("SELECT 'abc|")]
    [InlineData("SELECT N'a|bc'")]
    [InlineData("SELECT [Na|me]")]
    [InlineData("SELECT 1 -- com|ment")]
    [InlineData("SELECT 1 -- comment|")]
    [InlineData("/* bl|ock */ SELECT 1")]
    [InlineData("/* never closed|")]
    public async Task IsSuppressed_InsideLiteralsIdentifiersAndComments(string markedText)
    {
        var result = await CompleteAsync(markedText);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("SELECT 'abc'|")]
    [InlineData("SELECT [Name]|")]
    [InlineData("/* block */| SELECT 1")]
    [InlineData("SEL|")]
    [InlineData("|")]
    public async Task IsOffered_OutsideOpaqueRegions(string markedText)
    {
        var result = await CompleteAsync(markedText);
        Assert.Contains(result.Items, i => i.Label == "SELECT" && i.Kind == CompletionItemKind.Keyword);
    }

    [Fact]
    public async Task OffersDocumentVariables_ExceptTheOneBeingTyped()
    {
        var result = await CompleteAsync("DECLARE @Total INT, @Count INT; SET @To|");
        var variables = result.Items.Where(i => i.Kind == CompletionItemKind.Variable).Select(i => i.Label);
        Assert.Equal(["@Count", "@Total"], variables);
    }

    private async Task<CompletionResult> CompleteAsync(string markedText)
    {
        var position = markedText.IndexOf('|', StringComparison.Ordinal);
        var text = markedText.Remove(position, 1);
        var snapshot = new DocumentSnapshot("file:///test.sql", 1, text);
        return await _service.GetCompletionsAsync(snapshot, position, TestContext.Current.CancellationToken);
    }
}
