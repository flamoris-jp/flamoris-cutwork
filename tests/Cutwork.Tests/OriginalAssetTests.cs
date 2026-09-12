using Flamoris.Cutwork.Core;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class OriginalAssetTests
{
    [TestMethod]
    public void ConstructorAndReturnedCopiesCannotMutateOriginal()
    {
        var input = new byte[] { 1, 2, 3, 4 };
        var original = new OriginalAsset("test.png", new PixelSize(1, 1), 4, input);

        input[0] = 100;
        var firstCopy = original.CopyPixelBytes();
        firstCopy[1] = 101;
        var secondCopy = original.CopyPixelBytes();

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, secondCopy);
    }
}
