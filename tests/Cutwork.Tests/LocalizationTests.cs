using System.Globalization;
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
}
