using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Core.Settings;

namespace Wicked.Tests;

/// <summary>Every setting, made from WickedSettings itself: what the Settings editor lists and settings.json's schema says.</summary>
public sealed class SettingsSchemaTests
{
    [Fact]
    public void EverySettingIsListedWithADescription()
    {
        var keys = typeof(WickedSettings).GetProperties().Select(p => p.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false).OfType<JsonPropertyNameAttribute>().FirstOrDefault()?.Name).OfType<string>().ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), SettingsSchema.All.Select(s => s.Key).Order(StringComparer.Ordinal));
        // A setting added without words for the Settings editor fails here.
        Assert.Empty(SettingsSchema.All.Where(s => s.Description.Length < 10).Select(s => s.Key));
        Assert.DoesNotContain(SettingsSchema.All, s => s.Group == "Other");
    }

    [Fact]
    public void AChoiceOffersItsDefaultAndEachKindIsEditedItsOwnWay()
    {
        foreach (var s in SettingsSchema.All.Where(s => s.Kind == SettingKind.Choice))
        {
            Assert.Contains(JsonSerializer.Deserialize<string>(s.DefaultJson), s.Values);
        }

        Assert.Equal(SettingKind.Toggle, SettingsSchema.Find("editor.minimap.enabled")!.Kind);
        Assert.Equal(SettingKind.Number, SettingsSchema.Find("editor.fontSize")!.Kind);
        Assert.Equal(SettingKind.Integer, SettingsSchema.Find("editor.tabSize")!.Kind);
        Assert.Equal(SettingKind.Choice, SettingsSchema.Find("editor.wordWrap")!.Kind);
        Assert.Equal(SettingKind.Text, SettingsSchema.Find("workbench.colorTheme")!.Kind);
        Assert.Equal(SettingKind.Json, SettingsSchema.Find("editor.rulers")!.Kind);
        Assert.True(SettingsSchema.Find("huggingface.token")!.Secret);
    }

    [Theory]
    [InlineData("editor.fontSize", "Font Size")]
    [InlineData("editor.bracketPairColorization.enabled", "Bracket Pair Colorization: Enabled")]
    [InlineData("terminal.integrated.fontSize", "Font Size")]
    [InlineData("terminal.integrated.shellIntegration.enabled", "Shell Integration: Enabled")]
    [InlineData("files.autoSave", "Auto Save")]
    public void ASettingIsNamedAsVsCodeNamesIt(string key, string title) => Assert.Equal(title, SettingsSchema.TitleOf(key));

    [Fact]
    public void ASettingIsSetAndResetThroughItsKey()
    {
        var changed = SettingsSchema.With(new WickedSettings(), "editor.fontSize", JsonValue.Create(18.0))!;
        Assert.Equal(18, changed.FontSize);
        Assert.True(SettingsSchema.Find("editor.fontSize")!.IsModified(changed));
        var reset = SettingsSchema.With(changed, "editor.fontSize", null)!;
        Assert.Equal(14, reset.FontSize);
        Assert.False(SettingsSchema.Find("editor.fontSize")!.IsModified(reset));
        // A value of the wrong kind is refused, never half-applied.
        Assert.Null(SettingsSchema.With(changed, "editor.fontSize", JsonValue.Create("big")));
    }

    [Fact]
    public void TheSchemaDescribesEverySettingForSettingsJson()
    {
        using var doc = JsonDocument.Parse(SettingsSchema.JsonSchema());
        var properties = doc.RootElement.GetProperty("properties");
        Assert.Equal(SettingsSchema.All.Count, properties.EnumerateObject().Count());
        var wrap = properties.GetProperty("editor.wordWrap");
        Assert.Equal("string", wrap.GetProperty("type").GetString());
        Assert.Contains("bounded", wrap.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("off", wrap.GetProperty("default").GetString());
        Assert.Equal(6, properties.GetProperty("editor.fontSize").GetProperty("minimum").GetDouble());
        Assert.Equal("array", properties.GetProperty("editor.rulers").GetProperty("type").GetString());
    }
}

/// <summary>The Settings sheet's search over every setting, the way a user meets it.</summary>
public sealed class SettingsEditorWindowTests
{
    private static List<SettingRow> Rows(Window window) =>
        [.. window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "SettingRows").Items.OfType<SettingRow>()];

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task SearchFindsASettingAChangeIsMarkedAndKeptAndResetPutsItBack()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
            Input.Chord(window, Key.OemComma, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => shell.SettingsOpen, 5));

            // Typing in the search box lists every setting that answers it, in place of the sections.
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SettingsSearch");
            Assert.True(Ui.PumpUntil(() => search.IsFocused, 5), "the search does not have the keyboard");
            window.KeyTextInput("minimap");
            Assert.True(Ui.PumpUntil(() => Rows(window).Any(r => r.Key == "editor.minimap.enabled"), 5), "the search found nothing");
            Assert.All(Rows(window), r => Assert.Contains("minimap", r.Key + r.Heading + r.Description, StringComparison.OrdinalIgnoreCase));
            Assert.False(window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "SettingsSections").IsVisible);

            // Its switch changes the setting at once, marks the row and writes settings.json.
            var row = Rows(window).Single(r => r.Key == "editor.minimap.enabled");
            Ui.Pump(0.2);
            var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.Name == "SettingToggle" && t.DataContext == row && t.IsEffectivelyVisible);
            Input.Click(window, toggle);
            Assert.True(Ui.PumpUntil(() => !shell.Settings.Minimap && row.IsModified, 5), "the switch did not change the setting");
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("\"editor.minimap.enabled\": false", StringComparison.Ordinal), 5));

            // @modified lists just the changed ones; Reset puts it back.
            search.Text = "@modified";
            Assert.True(Ui.PumpUntil(() => Rows(window).Select(r => r.Key).SequenceEqual(["editor.minimap.enabled"]), 5), string.Join(", ", Rows(window).Select(r => r.Key)));
            Ui.Pump(0.2);
            var reset = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ResetSetting" && b.IsEffectivelyVisible);
            Input.Click(window, reset);
            Assert.True(Ui.PumpUntil(() => shell.Settings.Minimap && !row.IsModified, 5), "Reset did not put it back");

            // A section's button leaves the search for the sections.
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Editor"));
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "SettingsSections").IsVisible && search.Text == "", 5));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AllSettingsListsEveryOneAndAChoiceIsPickedFromItsValues()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
            shell.SettingsOpen = true;
            Ui.Pump(0.2);
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "JumpAll"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == SettingsSchema.All.Count, 5), $"{Rows(window).Count} of {SettingsSchema.All.Count}");

            var wrap = Rows(window).Single(r => r.Key == "editor.wordWrap");
            wrap.ChoiceValue = "bounded";
            Assert.True(Ui.PumpUntil(() => shell.Settings.WordWrap == "bounded" && wrap.IsModified, 5));
            window.Close();
            return 0;
        });
    }
}
