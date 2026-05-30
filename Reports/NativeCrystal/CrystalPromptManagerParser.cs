using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
internal static class CrystalPromptManagerParser
{
    private static readonly byte[] PromptManagerKey =
    [
        17, 221, 24, 150, 189, 74, 21, 205,
        191, 242, 84, 53, 3, 230, 118, 15
    ];

    public static void ApplyPromptMetadata(
        CrystalRptStream promptManagerStream,
        CrystalDataDefinitionModel dataDefinition)
    {
        string xml;
        try
        {
            xml = DecodePromptManagerXml(promptManagerStream.Bytes);
        }
        catch
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch
        {
            return;
        }

        var constrainedParameters = document
            .Descendants()
            .Where(element => element.Name.LocalName == "MetaObject")
            .Select(ReadPromptInfo)
            .Where(info => !string.IsNullOrWhiteSpace(info.Name))
            .ToDictionary(info => info.Name, StringComparer.OrdinalIgnoreCase);

        if (constrainedParameters.Count == 0)
        {
            return;
        }

        foreach (var parameter in dataDefinition.Parameters)
        {
            if (!constrainedParameters.TryGetValue(parameter.Name, out var promptInfo))
            {
                continue;
            }

            if (promptInfo.HasConstraint)
            {
                parameter.AllowCustomValues = false;
            }

            if (parameter.InitialValues.Count == 0)
            {
                foreach (var value in promptInfo.DefaultValues)
                {
                    var formattedValue = FormatPromptValue(value, parameter.ValueType);
                    if (formattedValue is null)
                    {
                        continue;
                    }

                    parameter.InitialValues.Add(new CrystalParameterDefaultValueModel
                    {
                        Value = formattedValue
                    });
                }
            }
        }
    }

    private static PromptInfo ReadPromptInfo(XElement metaObject)
    {
        var type = metaObject
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Type")
            ?.Value;
        if (!string.Equals(type, "Parameter", StringComparison.OrdinalIgnoreCase))
        {
            return PromptInfo.Empty;
        }

        var parameterObject = metaObject
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Object");
        if (parameterObject is null)
        {
            return PromptInfo.Empty;
        }

        var name = parameterObject
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Name")
            ?.Value ?? "";
        var hasConstraint = parameterObject
            .Elements()
            .Any(element => element.Name.LocalName == "ConstraintRef");
        var defaultValues = parameterObject
            .Elements()
            .Where(element => element.Name.LocalName == "DefaultValues")
            .SelectMany(element => element.Descendants())
            .Where(element => element.Name.LocalName == "Value")
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return new PromptInfo(name, hasConstraint, defaultValues);
    }

    private static string? FormatPromptValue(string value, int valueType)
    {
        if (valueType is 6 or 7 or 16)
        {
            return decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                ? number.ToString("0.0#############################", System.Globalization.CultureInfo.InvariantCulture)
                : null;
        }

        if (valueType == 8 &&
            bool.TryParse(value, out var boolean))
        {
            return boolean ? "true" : "false";
        }

        return value;
    }

    private static string DecodePromptManagerXml(byte[] bytes)
    {
        var decrypted = DecryptPromptManager(bytes);
        using var input = new MemoryStream(decrypted);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static byte[] DecryptPromptManager(byte[] bytes)
    {
        using var aes = Aes.Create();
        aes.Key = ReverseWords(PromptManagerKey);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var state = new byte[16];
        var output = new byte[bytes.Length];
        var transformedState = new byte[16];
        var transformedKeyStream = new byte[16];
        var keyStream = new byte[16];

        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            ReverseWords(state, transformedState);
            encryptor.TransformBlock(transformedState, 0, transformedState.Length, transformedKeyStream, 0);
            ReverseWords(transformedKeyStream, keyStream);

            var count = Math.Min(16, bytes.Length - offset);
            for (var i = 0; i < count; i++)
            {
                var cipher = bytes[offset + i];
                output[offset + i] = (byte)(cipher ^ keyStream[i]);
                state[i] = cipher;
            }
        }

        return output;
    }

    private static byte[] ReverseWords(byte[] bytes)
    {
        var output = new byte[bytes.Length];
        ReverseWords(bytes, output);
        return output;
    }

    private static void ReverseWords(byte[] source, byte[] destination)
    {
        for (var i = 0; i < source.Length; i += 4)
        {
            destination[i + 0] = source[i + 3];
            destination[i + 1] = source[i + 2];
            destination[i + 2] = source[i + 1];
            destination[i + 3] = source[i + 0];
        }
    }

    private sealed record PromptInfo(string Name, bool HasConstraint, IReadOnlyList<string> DefaultValues)
    {
        public static PromptInfo Empty { get; } = new("", false, []);
    }
}
