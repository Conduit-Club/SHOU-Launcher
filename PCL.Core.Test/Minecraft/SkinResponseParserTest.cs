using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.Skin;

namespace PCL.Core.Test.Minecraft;

[TestClass]
public sealed class SkinResponseParserTest
{
    [TestMethod]
    public void ParsesUppercaseSkinKeysAndPreservesUrlCase()
    {
        const string expectedUrl = "https://Textures.Minecraft.net/skin/MixedCase.PNG";
        var response = CreateProfileResponse(
            Encode("{\"textures\":{\"SKIN\":{\"URL\":\"" + expectedUrl + "\"}}}"));

        var actualUrl = SkinResponseParser.TryGetSkinUrl(response);

        Assert.AreEqual(expectedUrl, actualUrl);
    }

    [TestMethod]
    public void EmptyTexturesMeansNoCustomSkin()
    {
        var response = CreateProfileResponse(Encode("{\"textures\":{}}"));

        Assert.IsNull(SkinResponseParser.TryGetSkinUrl(response));
    }

    [TestMethod]
    public void InvalidBase64RemainsAFormatError()
    {
        var response = CreateProfileResponse("not-base64");

        Assert.ThrowsExactly<FormatException>(() => SkinResponseParser.TryGetSkinUrl(response));
    }

    [TestMethod]
    public void ErrorResponseIsNotTreatedAsNoCustomSkin()
    {
        const string response = "{\"error\":\"ForbiddenOperationException\",\"errorMessage\":\"Forbidden\",\"properties\":[]}";

        Assert.ThrowsExactly<InvalidDataException>(() => SkinResponseParser.TryGetSkinUrl(response));
    }

    [TestMethod]
    public void MissingPropertiesOnAValidProfileMeansNoCustomSkin()
    {
        const string response = "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"Player\"}";

        Assert.IsNull(SkinResponseParser.TryGetSkinUrl(response));
    }

    [TestMethod]
    public void MalformedTextureTypesAreRejected()
    {
        var malformedTextures = new[]
        {
            "{\"textures\":[]}",
            "{\"textures\":{\"skin\":[]}}",
            "{\"textures\":{\"skin\":{\"url\":123}}}"
        };

        foreach (var textures in malformedTextures)
        {
            var response = CreateProfileResponse(Encode(textures));
            Assert.ThrowsExactly<InvalidDataException>(() => SkinResponseParser.TryGetSkinUrl(response));
        }
    }

    [TestMethod]
    public void SessionProfileIdRemovesUuidHyphens()
    {
        const string uuid = "4b14e9d9-57a6-496e-b8a2-baf500aec1d7";

        Assert.AreEqual("4b14e9d957a6496eb8a2baf500aec1d7",
            SkinProfileId.NormalizeForSession(uuid));
    }

    [TestMethod]
    public void SessionProfileIdPreservesCustomIdentifiers()
    {
        Assert.AreEqual("role-id", SkinProfileId.NormalizeForSession(" role-id "));
    }

    private static string CreateProfileResponse(string textureValue)
    {
        return "{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"Player\",\"properties\":[{" +
               "\"name\":\"textures\",\"value\":\"" + textureValue + "\"}]}";
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
}
