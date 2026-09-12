using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Imaging.Diagnostics;

/// <summary>Opt-in diagnostic fixture through ordinary edit commands, not a user-facing tool.</summary>
public static class DeveloperLayerFixture
{
    public static EditCommand[] Create(CutworkDocument document)
    {
        var width = Math.Clamp(document.Dimensions.Width / 4, 1, 256);
        var height = Math.Clamp(document.Dimensions.Height / 4, 1, 256);
        var bounds = new DocumentRect((document.Dimensions.Width - width) / 2,
            (document.Dimensions.Height - height) / 2, width, height);
        var mask = new byte[width * height]; Array.Fill(mask, (byte)255);
        var patch = new byte[width * height * 4]; var repair = new byte[patch.Length];
        for (var offset = 0; offset < patch.Length; offset += 4)
        {
            patch[offset] = 200; patch[offset + 1] = 100; patch[offset + 2] = 40; patch[offset + 3] = 170;
            repair[offset] = 40; repair[offset + 1] = 120; repair[offset + 2] = 220; repair[offset + 3] = 255;
        }
        return [new AddLayer(new PartLayer(bounds, mask)),
            new AddLayer(new PatchLayer(bounds, patch)), new AddLayer(new RepairLayer(bounds, repair))];
    }
}
