using Flamoris.Cutwork.Imaging;

namespace Flamoris.Cutwork.Tests;

[TestClass]
public sealed class ImageImportTests
{
    private const string Png3By2 = "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFUlEQVR4nGOUC+hhYGBgYGBgYoABABFiAP4kJh6cAAAAAElFTkSuQmCC";
    private const string Jpeg3By2 = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAACAAMDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDh6KKK+iPEP//Z";

    [TestMethod]
    [DataRow("sample.png", Png3By2)]
    [DataRow("sample.jpg", Jpeg3By2)]
    public void ImportPreservesPngAndJpegDimensions(string fileName, string base64)
    {
        var path = WriteTemporaryFile(fileName, Convert.FromBase64String(base64));
        try
        {
            var result = new ImageImportService().Import(path);

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(3, result.Original!.Dimensions.Width);
            Assert.AreEqual(2, result.Original.Dimensions.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void UnsupportedExtensionHasStableErrorCategory()
    {
        var result = new ImageImportService().Import("sample.gif");

        Assert.AreEqual(ImageImportError.UnsupportedFormat, result.Error);
    }

    [TestMethod]
    public void InvalidSupportedFileHasStableErrorCategory()
    {
        var path = WriteTemporaryFile("invalid.png", new byte[] { 1, 2, 3, 4 });
        try
        {
            var result = new ImageImportService().Import(path);

            Assert.AreEqual(ImageImportError.InvalidImage, result.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemporaryFile(string fileName, byte[] contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cutwork-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, contents);
        return path;
    }
}
