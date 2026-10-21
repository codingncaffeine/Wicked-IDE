using Wicked.Core.Extensions;

namespace Wicked.Tests.Extensions;

public sealed class ManifestTests
{
    [Theory]
    [InlineData("^1.0", true)]
    [InlineData("^1.0.0", true)]
    [InlineData("~1.0", true)]
    [InlineData("1.0", true)]
    [InlineData("=1.0", true)]
    [InlineData(">=1.0", true)]
    [InlineData(">=0.9 <2.0", true)]
    [InlineData("*", true)]
    [InlineData("^1.1", false)]
    [InlineData("^2.0", false)]
    [InlineData("~1.1", false)]
    [InlineData(">1.0", false)]
    [InlineData("<1.0", false)]
    [InlineData("0.9", false)]
    public void ARangeSaysWhetherThisApiIsInside(string range, bool inside)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed));
        Assert.Equal(inside, parsed.Contains(WickedApiVersion.Current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("^one")]
    [InlineData("^1.2.3.4")]
    [InlineData(">=")]
    public void ANonsenseRangeIsRefused(string range) => Assert.False(VersionRange.TryParse(range, out _));

    [Fact]
    public void AGoodManifestReadsEveryField()
    {
        var json = ExtensionKit.Manifest(contributes: """
            { "themes": [ { "label": "Dusk", "path": "themes/dusk.json" } ],
              "grammars": [ { "language": "foo", "scopeName": "source.foo", "path": "g.json", "extensions": [".foo"] } ],
              "snippets": [ { "language": "foo", "path": "s/foo.json" } ],
              "languageServers": [ { "id": "foo-ls", "languages": ["foo"], "programs": ["foo-ls"], "arguments": ["--stdio"] } ],
              "keybindings": [ { "command": "hello.say", "key": "ctrl+alt+h" } ],
              "settings": { "hello.greeting": "Hi" } }
            """, activation: ["onLanguage:foo"]);
        var (manifest, problem) = ExtensionManifest.Parse(json);
        Assert.Null(problem);
        Assert.NotNull(manifest);
        Assert.Equal("acme.hello", manifest.FullId);
        Assert.Equal("acme.hello-1.0.0", manifest.FolderName);
        Assert.Equal("Hello.dll", manifest.Main);
        Assert.Equal(["foo"], manifest.ActivationLanguages);
        Assert.Equal("Dusk", Assert.Single(manifest.Contributes.Themes).Label);
        Assert.Equal([".foo"], Assert.Single(manifest.Contributes.Grammars).Extensions);
        Assert.Equal("foo-ls", Assert.Single(manifest.Contributes.LanguageServers).Id);
        Assert.Equal("Hi", manifest.Contributes.Settings["hello.greeting"].GetString());
    }

    [Fact]
    public void AnExtensionForAnotherApiVersionIsRefusedWithTheRangeInTheMessage()
    {
        var (manifest, problem) = ExtensionManifest.Parse(ExtensionKit.Manifest(engine: "^2.0"));
        Assert.Null(manifest);
        Assert.Contains("^2.0", problem);
        Assert.Contains(WickedApiVersion.Text, problem);
    }

    [Theory]
    [InlineData("""{ "id": "../evil", "name": "x", "version": "1.0.0", "publisher": "a", "engines": { "wicked": "^1.0" } }""", "id")]
    [InlineData("""{ "id": "x", "name": "x", "version": "1.0.0", "publisher": "A B", "engines": { "wicked": "^1.0" } }""", "publisher")]
    [InlineData("""{ "id": "x", "name": "x", "version": "one", "publisher": "a", "engines": { "wicked": "^1.0" } }""", "version")]
    [InlineData("""{ "id": "x", "name": "x", "version": "1.0.0", "publisher": "a" }""", "engines")]
    [InlineData("""{ "id": "x", "name": "x", "version": "1.0.0", "publisher": "a", "engines": { "wicked": "^1.0" }, "main": "../outside.dll" }""", "main")]
    [InlineData("""{ "id": "x", "name": "x", "version": "1.0.0", "publisher": "a", "engines": { "wicked": "^1.0" }, "contributes": { "themes": [ { "label": "t", "path": "/etc/passwd" } ] } }""", "inside the extension")]
    [InlineData("""{ "id": "x", "name": "x", "version": "1.0.0", "publisher": "a", "engines": { "wicked": "^1.0" }, "activationEvents": ["onMonday"] }""", "activation")]
    [InlineData("{ not json", "could not be read")]
    public void AnUnsafeOrIncompleteManifestSaysWhatIsWrong(string json, string mentions)
    {
        var (manifest, problem) = ExtensionManifest.Parse(json);
        Assert.Null(manifest);
        Assert.Contains(mentions, problem, StringComparison.OrdinalIgnoreCase);
    }
}
