using System.Text.Json;
using Flamoris.Cutwork.Mcp;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class CatalogTests
{
    [TestMethod]
    public void HubCatalogMatchesRuntimeSchemasDescriptionsAndEditGuards()
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"cutwork-catalog.json")));
        var tools=json.RootElement.GetProperty("tools").EnumerateArray().ToDictionary(x=>x.GetProperty("name").GetString()!);
        Assert.AreEqual(LiveSchema.Schemas.Count+3,tools.Count);
        foreach(var pair in LiveSchema.Schemas)
        {
            var tool=tools[pair.Key];var wrapper=tool.GetProperty("input_schema");
            Assert.IsTrue(JsonElement.DeepEquals(pair.Value,wrapper.GetProperty("properties").GetProperty("input")),pair.Key);
            Assert.AreEqual(LiveSchema.Description(pair.Key),tool.GetProperty("description").GetString());
            var required=wrapper.GetProperty("required").EnumerateArray().Select(x=>x.GetString()).ToArray();
            Assert.AreEqual(LiveSchema.IsEdit(pair.Key),required.Contains("guard"),pair.Key);
        }
    }
}
