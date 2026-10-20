using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.Minecraft.Skin;

/// <summary>
///     Parses the profile response returned by a Minecraft session server.
/// </summary>
public static class SkinResponseParser
{
    /// <summary>
    ///     Gets the custom skin URL, or <see langword="null" /> when the valid profile has no custom skin.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The response is not a profile response or contains invalid texture data.
    /// </exception>
    public static string? TryGetSkinUrl(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidDataException("Skin response is empty.");

        JsonObject profile;
        try
        {
            profile = JsonNode.Parse(response)?.AsObject() ??
                      throw new InvalidDataException("Skin response is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Skin response is not valid JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("Skin response is not a JSON object.", ex);
        }

        var hasServerError = GetProperty(profile, "error") is not null ||
                             GetProperty(profile, "errorMessage") is not null;
        if (hasServerError)
            throw new InvalidDataException("Skin response contains a server error.");

        var hasProfileIdentity = !string.IsNullOrWhiteSpace(GetText(profile, "id")) &&
                                 !string.IsNullOrWhiteSpace(GetText(profile, "name"));

        JsonArray properties;
        try
        {
            var propertiesNode = GetProperty(profile, "properties");
            if (propertiesNode is null)
            {
                if (hasProfileIdentity) return null;
                throw new InvalidDataException("Skin response has no properties.");
            }

            properties = propertiesNode.AsArray();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("Skin response properties are invalid.", ex);
        }

        string? textureValue = null;
        foreach (var property in properties)
        {
            if (property is not JsonObject propertyObject ||
                !string.Equals(GetText(propertyObject, "name"), "textures", StringComparison.OrdinalIgnoreCase))
                continue;

            textureValue = GetText(propertyObject, "value");
            break;
        }

        if (string.IsNullOrWhiteSpace(textureValue))
        {
            if (hasProfileIdentity) return null;
            throw new InvalidDataException("Skin response has no textures property.");
        }

        // Let FormatException remain visible to the caller: a malformed property is different
        // from a valid profile that simply has no custom skin.
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(textureValue));
        JsonObject texturesResponse;
        try
        {
            texturesResponse = JsonNode.Parse(decoded)?.AsObject() ??
                               throw new InvalidDataException("Skin texture data is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Skin texture data is not valid JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("Skin texture data is not a JSON object.", ex);
        }

        var texturesNode = GetProperty(texturesResponse, "textures");
        if (texturesNode is null) return null;
        if (texturesNode is not JsonObject textures)
            throw new InvalidDataException("Skin texture data has an invalid textures object.");

        var skinNode = GetProperty(textures, "skin");
        if (skinNode is null) return null;
        if (skinNode is not JsonObject skin)
            throw new InvalidDataException("Skin texture data has an invalid skin object.");

        var urlNode = GetProperty(skin, "url");
        if (urlNode is null) return null;
        if (urlNode is not JsonValue urlValue || !urlValue.TryGetValue<string>(out var url))
            throw new InvalidDataException("Skin texture data has an invalid skin URL.");

        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    private static JsonNode? GetProperty(JsonObject? json, string name)
    {
        if (json is null) return null;
        foreach (var property in json)
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private static string? GetText(JsonObject? json, string name)
    {
        var value = GetProperty(json, name);
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
            return text;
        return value?.ToString();
    }
}
