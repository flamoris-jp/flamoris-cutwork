using System.Globalization;
using System.Text.RegularExpressions;
using Flamoris.Cutwork.Core;
using Flamoris.Cutwork.App;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class LocalizationTests
{
    [TestMethod]
    public void JapaneseAndEnglishCatalogsHaveIdenticalKeys()
    {
        var japanese = LocalizationService.GetKeys(new CultureInfo("ja-JP"));
        var english = LocalizationService.GetKeys(new CultureInfo("en-US"));

        CollectionAssert.AreEquivalent(japanese.ToArray(), english.ToArray());
        Assert.IsTrue(japanese.Count > 0);
    }

    [TestMethod]
    public void UnsupportedLocaleFallsBackToJapanese()
    {
        var localization = new LocalizationService();
        localization.SetCulture(new CultureInfo("fr-FR"));

        Assert.AreEqual("ja-JP", localization.Culture.Name);
        Assert.AreEqual("ファイル", localization["Menu_File"]);
    }

    [TestMethod]
    public void CatalogPlaceholdersAndEditErrorCoverageMatch()
    {
        var japanese = new LocalizationService();
        var english = new LocalizationService(); english.SetCulture(new CultureInfo("en-US"));
        var keys = LocalizationService.GetKeys(new CultureInfo("ja-JP"));
        foreach (var key in keys)
        {
            var ja = Regex.Matches(japanese[key], @"\{(\d+)[^}]*\}").Select(match => match.Groups[1].Value).ToArray();
            var en = Regex.Matches(english[key], @"\{(\d+)[^}]*\}").Select(match => match.Groups[1].Value).ToArray();
            CollectionAssert.AreEquivalent(ja, en, key);
        }
        foreach (var error in Enum.GetValues<EditError>()) Assert.IsTrue(keys.Contains($"EditError_{error}"));
    }

    [TestMethod]
    public void CloneModeLabelsAreLocalizedInBothCatalogs()
    {
        var japanese = new LocalizationService();
        var english = new LocalizationService();
        english.SetCulture(new CultureInfo("en-US"));

        Assert.AreEqual("固定クローン", japanese["CloneTool_Mode_Fixed"]);
        Assert.AreEqual("移動クローン", japanese["CloneTool_Mode_Offset"]);
        Assert.AreEqual("Fixed Clone", english["CloneTool_Mode_Fixed"]);
        Assert.AreEqual("Offset Clone", english["CloneTool_Mode_Offset"]);
    }
}
