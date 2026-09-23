using System.Text;
using System.Xml;

namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>Retains XML-inexpressible source strings while emitting readable, legal XML.</summary>
internal sealed class CrystalXmlCharacterWriter(XmlWriter inner) : XmlWriter
{
    public Dictionary<string, string> EscapedValues { get; } = new(StringComparer.Ordinal);

    public override void WriteString(string? text)
    {
        if (text is null) { inner.WriteString(null); return; }
        StringBuilder? escaped = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                escaped?.Append(ch).Append(text[i + 1]);
                i++;
                continue;
            }
            if (XmlConvert.IsXmlChar(ch)) escaped?.Append(ch);
            else
            {
                escaped ??= new StringBuilder(text[..i]);
                escaped.Append("_x").Append(((int)ch).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append('_');
            }
        }
        if (escaped is null) { inner.WriteString(text); return; }
        var value = escaped.ToString();
        EscapedValues.TryAdd(text, value);
        inner.WriteString(value);
    }

    internal static string PreserveUtf16(string text)
    {
        var bytes = new byte[checked(text.Length * 2)];
        for (var i = 0; i < text.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), text[i]);
        return Convert.ToBase64String(bytes);
    }

    public override WriteState WriteState => inner.WriteState;
    public override string? LookupPrefix(string ns) => inner.LookupPrefix(ns);
    public override void Flush() => inner.Flush();
    public override void Close() => inner.Close();
    public override void WriteStartDocument() => inner.WriteStartDocument();
    public override void WriteStartDocument(bool standalone) => inner.WriteStartDocument(standalone);
    public override void WriteEndDocument() => inner.WriteEndDocument();
    public override void WriteDocType(string name, string? pubid, string? sysid, string? subset) => inner.WriteDocType(name, pubid, sysid, subset);
    public override void WriteStartElement(string? prefix, string localName, string? ns) => inner.WriteStartElement(prefix, localName, ns);
    public override void WriteEndElement() => inner.WriteEndElement();
    public override void WriteFullEndElement() => inner.WriteFullEndElement();
    public override void WriteStartAttribute(string? prefix, string localName, string? ns) => inner.WriteStartAttribute(prefix, localName, ns);
    public override void WriteEndAttribute() => inner.WriteEndAttribute();
    public override void WriteCData(string? text) => WriteString(text);
    public override void WriteComment(string? text) => inner.WriteComment(text);
    public override void WriteProcessingInstruction(string name, string? text) => inner.WriteProcessingInstruction(name, text);
    public override void WriteEntityRef(string name) => inner.WriteEntityRef(name);
    public override void WriteCharEntity(char ch) => WriteString(ch.ToString());
    public override void WriteWhitespace(string? ws) => inner.WriteWhitespace(ws);
    public override void WriteSurrogateCharEntity(char lowChar, char highChar) => inner.WriteSurrogateCharEntity(lowChar, highChar);
    public override void WriteChars(char[] buffer, int index, int count) => WriteString(new string(buffer, index, count));
    public override void WriteRaw(char[] buffer, int index, int count) => inner.WriteRaw(buffer, index, count);
    public override void WriteRaw(string data) => inner.WriteRaw(data);
    public override void WriteBase64(byte[] buffer, int index, int count) => inner.WriteBase64(buffer, index, count);
}
