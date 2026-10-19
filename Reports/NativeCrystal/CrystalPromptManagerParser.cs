using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>
/// Reads Crystal's <c>PromptManager</c> stream: the definitions behind the report's own
/// "Enter Parameter Values" dialog. Per parameter it yields the prompt constraint, the default
/// values and the dynamic list of values (LOV) the prompt's pick list is filled from.
/// </summary>
/// <remarks>
/// The stream is a sequence of XML segments stored back to back, each encrypted and
/// zlib-compressed on its own: (1) Parameter objects (Name, ValueType, PromptGroupRef,
/// ConstraintRef, DefaultValues), (2) PromptGroup objects (the LOVDS reference and the group's
/// Prompts in order, each with its FieldID and SortingType), (3) LOVDS objects (the LOV field tree
/// and the query definition naming the tables) — present only when the report has dynamic lists —
/// and (4) the prompt groups again in an alternate form. The cipher state restarts at every segment
/// boundary, so each segment is decrypted from its own offset, and a segment ends where its zlib
/// stream ends.
/// </remarks>
[UnsupportedOSPlatform("browser")]
public static class CrystalPromptManagerParser
{
    private const int MaxSegments = 16;
    private const int MaxInflatedBytes = 16 * 1024 * 1024;
    private static readonly XName XsiType = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type";

    private static readonly byte[] PromptManagerKey =
    [
        17, 221, 24, 150, 189, 74, 21, 205,
        191, 242, 84, 53, 3, 230, 118, 15
    ];

    /// <summary>
    /// Decodes the stream's XML segments in order. Decoding stops at the first segment that does
    /// not decrypt, inflate and parse cleanly and returns the segments before it; malformed data
    /// never throws.
    /// </summary>
    public static IReadOnlyList<CrystalPromptManagerSegment> DecodeSegments(byte[] promptManagerBytes)
    {
        return DecodeSegments(promptManagerBytes, CancellationToken.None);
    }

    /// <inheritdoc cref="DecodeSegments(byte[])"/>
    /// <remarks>Cancellation is the one exception that leaves the method.</remarks>
    public static IReadOnlyList<CrystalPromptManagerSegment> DecodeSegments(
        byte[] promptManagerBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptManagerBytes);
        return DecodeSegmentDocuments(promptManagerBytes, cancellationToken).Select(entry => entry.Segment).ToArray();
    }

    internal static void ApplyPromptMetadata(
        CrystalRptStream promptManagerStream,
        CrystalDataDefinitionModel dataDefinition,
        CancellationToken cancellationToken = default)
    {
        List<(CrystalPromptManagerSegment Segment, XDocument Document)> segments;
        try
        {
            segments = DecodeSegmentDocuments(promptManagerStream.Bytes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return;
        }

        var catalog = new PromptCatalog();
        foreach (var (_, document) in segments)
        {
            try
            {
                catalog.Add(document);
            }
            catch
            {
                // A damaged segment only costs the metadata it carried.
            }
        }

        if (catalog.Parameters.Count == 0)
        {
            return;
        }

        foreach (var parameter in dataDefinition.Parameters)
        {
            if (!catalog.Parameters.TryGetValue(parameter.Name, out var promptInfo))
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

            try
            {
                if (parameter.PickList is null)
                {
                    parameter.PickList = catalog.ResolvePickList(promptInfo, out var cascadingGroup);
                    if (cascadingGroup is not null)
                    {
                        dataDefinition.ParseWarnings.Add(
                            $"Prompt '{parameter.Name}' is a lower level of the cascading prompt group '{cascadingGroup}': Crystal lists only the values under those chosen above it, so its list of values is not converted and is not narrowed by the levels above it.");
                    }
                }
            }
            catch
            {
                // An unreadable list of values leaves the prompt without one, as before.
            }
        }
    }

    /// <summary>The prompt objects of all segments, keyed the way they reference each other.</summary>
    private sealed class PromptCatalog
    {
        private readonly Dictionary<string, PromptGroupInfo> _promptGroups = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LovSourceInfo> _lovSources = new(StringComparer.Ordinal);

        public Dictionary<string, PromptInfo> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Add(XDocument document)
        {
            // References (id / content-id attributes) point at the first element body with that
            // id that has children; later occurrences are empty reference stubs.
            var definitions = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var element in document.Descendants())
            {
                var id = (string?)element.Attribute("id");
                if (id is not null && element.HasElements)
                {
                    definitions.TryAdd(id, element);
                }
            }

            XElement Resolve(XElement element)
            {
                var key = (string?)element.Attribute("content-id") ?? (string?)element.Attribute("id");
                return key is not null && definitions.TryGetValue(key, out var body) ? body : element;
            }

            foreach (var metaObject in document.Descendants().Where(element => element.Name.LocalName == "MetaObject"))
            {
                var type = ChildValue(metaObject, "Type");
                var body = Child(metaObject, "Object");
                if (body is null)
                {
                    continue;
                }

                var id = ChildValue(metaObject, "ID");
                if (string.Equals(type, "Parameter", StringComparison.OrdinalIgnoreCase))
                {
                    var info = ReadPromptInfo(body, Resolve);
                    if (!string.IsNullOrWhiteSpace(info.Name))
                    {
                        Parameters.TryAdd(info.Name, info);
                    }
                }
                else if (string.Equals(type, "PromptGroup", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
                {
                    // The alternate-form segment repeats the groups under the same IDs; the first form wins.
                    _promptGroups.TryAdd(id, ReadPromptGroup(body, Resolve));
                }
                else if (string.Equals(type, "LOVDS", StringComparison.OrdinalIgnoreCase) && id.Length > 0)
                {
                    _lovSources.TryAdd(id, ReadLovSource(body, Resolve));
                }
            }
        }

        /// <param name="prompt">The parameter's prompt definition.</param>
        /// <param name="cascadingGroup">The group's name when the prompt is a lower level of a cascading group (no list then).</param>
        public CrystalParameterPickListModel? ResolvePickList(PromptInfo prompt, out string? cascadingGroup)
        {
            cascadingGroup = null;
            if (prompt.PromptGroupRef is null ||
                !_promptGroups.TryGetValue(prompt.PromptGroupRef, out var group) ||
                group.LovSourceRef is null ||
                !_lovSources.TryGetValue(group.LovSourceRef, out var source) ||
                source.Fields.Count == 0)
            {
                return null;
            }

            // The parameter's PromptGroupConstraint names the prompt of the group that feeds it. A
            // group of one prompt needs no index; in a larger group a missing or out-of-range index
            // leaves the parameter without a list rather than borrowing another prompt's.
            GroupPrompt groupPrompt;
            if (prompt.PromptIndex is { } index)
            {
                if (index < 0 || index >= group.Prompts.Count)
                {
                    return null;
                }

                groupPrompt = group.Prompts[index];
            }
            else if (group.Prompts.Count == 1)
            {
                groupPrompt = group.Prompts[0];
            }
            else
            {
                return null;
            }

            // The prompt names the LOV field that feeds it; a field the source does not hold gives no list.
            var field = source.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, groupPrompt.FieldId, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                return null;
            }

            if (field.IsCascadingChild)
            {
                // A cascading level lists only the values under the ones chosen at the level above
                // it, so the whole column would offer values Crystal never shows there. Without a
                // list of its own the prompt keeps the list it had before (from its record-selection
                // binding or the host), which a dependent pick-list provider may still narrow.
                cascadingGroup = group.Name;
                return null;
            }

            var table = field.Value.TableAlias;
            if (source.Tables.TryGetValue(table, out var lovTable))
            {
                if (lovTable.IsCommand)
                {
                    // A SQL command, not a table: there is nothing to select the list from.
                    return null;
                }

                table = lovTable.Name;
            }

            // A description from another table would need the LOV's join graph; list values only.
            var description = field.Description is { } descriptionField &&
                              string.Equals(descriptionField.TableAlias, field.Value.TableAlias, StringComparison.OrdinalIgnoreCase)
                ? descriptionField.Name
                : null;

            return new CrystalParameterPickListModel
            {
                Table = table,
                ValueField = field.Value.Name,
                DescriptionField = description,
                Sort = groupPrompt.Sort
            };
        }
    }

    private static PromptInfo ReadPromptInfo(XElement parameterObject, Func<XElement, XElement> resolve)
    {
        var name = ChildValue(parameterObject, "Name");
        var constraints = parameterObject
            .Elements()
            .Where(element => element.Name.LocalName == "ConstraintRef")
            .ToArray();
        var hasConstraint = constraints.Length > 0;

        // A PromptGroupConstraint binds the parameter to prompts From..To of its group; the value
        // is the one chosen at prompt To (the last level of a cascading path).
        int? promptIndex = null;
        var groupConstraint = constraints.FirstOrDefault(element =>
            ((string?)element.Attribute(XsiType) ?? "").EndsWith("PromptGroupConstraint", StringComparison.OrdinalIgnoreCase));
        if (groupConstraint is not null &&
            int.TryParse(ChildValue(resolve(groupConstraint), "To").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
        {
            promptIndex = to;
        }
        var defaultValues = parameterObject
            .Elements()
            .Where(element => element.Name.LocalName == "DefaultValues")
            .SelectMany(element => element.Descendants())
            .Where(element => element.Name.LocalName == "Value")
            .Select(element => element.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var promptGroupRef = ChildValue(parameterObject, "PromptGroupRef");

        return new PromptInfo(name, hasConstraint, defaultValues, promptGroupRef.Length > 0 ? promptGroupRef : null, promptIndex);
    }

    private static PromptGroupInfo ReadPromptGroup(XElement groupObject, Func<XElement, XElement> resolve)
    {
        var name = ChildValue(groupObject, "Name");
        var lovSourceRef = ChildValue(groupObject, "LOVDS");

        // Every Prompt of the group in order (a cascading group has one per level); a Prompt
        // nested inside another is part of that prompt's definition, not a level of its own.
        var prompts = groupObject
            .Descendants()
            .Where(element => element.Name.LocalName == "Prompt" &&
                              !element.Ancestors().TakeWhile(ancestor => ancestor != groupObject).Any(ancestor => ancestor.Name.LocalName == "Prompt"))
            .Select(element => ReadGroupPrompt(resolve(element)))
            .ToArray();

        return new PromptGroupInfo(name, lovSourceRef.Length > 0 ? lovSourceRef : null, prompts);
    }

    private static GroupPrompt ReadGroupPrompt(XElement prompt)
    {
        var sortingType = prompt.Descendants().FirstOrDefault(element => element.Name.LocalName == "SortingType")?.Value.Trim();
        var sort = string.Equals(sortingType, "Descending", StringComparison.OrdinalIgnoreCase) ? "Descending"
            : string.Equals(sortingType, "NoSort", StringComparison.OrdinalIgnoreCase) ? "NoSort"
            : "Ascending";

        return new GroupPrompt(ChildValue(prompt, "FieldID"), sort);
    }

    private static LovSourceInfo ReadLovSource(XElement lovObject, Func<XElement, XElement> resolve)
    {
        // The LOV field tree: a LOVField holds Name, the DB Field it lists, an optional Sibling
        // (the description LOVField) and, in a cascading list, a Child LOVField for the next
        // level. Every LOVField-shaped element in the tree is collected so a prompt can pick its
        // own by FieldID; one reached through a Child is marked as a cascading level.
        var fields = new List<LovField>();
        var root = Child(lovObject, "Field");
        if (root is not null)
        {
            foreach (var element in root.DescendantsAndSelf().Where(element => element.Name.LocalName is "Field" or "Sibling" or "Child"))
            {
                var lovField = resolve(element);
                var value = ReadLovFieldValue(lovField, resolve);
                if (value is null)
                {
                    // A DB field body (Name + TableAlias), not a LOV field.
                    continue;
                }

                var sibling = Child(lovField, "Sibling");
                var description = sibling is null ? null : ReadLovFieldValue(resolve(sibling), resolve);
                var isCascadingChild = element
                    .AncestorsAndSelf()
                    .TakeWhile(ancestor => ancestor != root)
                    .Any(ancestor => ancestor.Name.LocalName == "Child");
                fields.Add(new LovField(ChildValue(lovField, "Name"), value, description, isCascadingChild));
            }
        }

        var tables = new Dictionary<string, LovTable>(StringComparer.OrdinalIgnoreCase);
        var query = Child(lovObject, "QueryDefinition");
        var tablesElement = query is null ? null : Child(resolve(query), "Tables");
        if (tablesElement is not null)
        {
            foreach (var tableElement in tablesElement.Elements().Where(element => element.Name.LocalName == "Table"))
            {
                var table = resolve(tableElement);
                var name = ChildValue(table, "Name");
                if (name.Length == 0)
                {
                    continue;
                }

                var alias = ChildValue(table, "Alias");
                var xsiType = (string?)table.Attribute(XsiType) ?? "";
                var isCommand = xsiType.EndsWith("CommandTable", StringComparison.OrdinalIgnoreCase) ||
                                Child(table, "CommandText") is not null;
                tables.TryAdd(alias.Length > 0 ? alias : name, new LovTable(name, isCommand));
            }
        }

        return new LovSourceInfo(fields, tables);
    }

    private static DbFieldRef? ReadLovFieldValue(XElement lovField, Func<XElement, XElement> resolve)
    {
        var dbField = Child(lovField, "Field");
        if (dbField is null)
        {
            return null;
        }

        var body = resolve(dbField);
        var tableAlias = ChildValue(body, "TableAlias");
        var name = ChildValue(body, "Name");
        return tableAlias.Length > 0 && name.Length > 0 ? new DbFieldRef(tableAlias, name) : null;
    }

    private static XElement? Child(XElement parent, string localName)
    {
        return parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
    }

    private static string ChildValue(XElement parent, string localName)
    {
        return Child(parent, localName)?.Value ?? "";
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

    private static List<(CrystalPromptManagerSegment Segment, XDocument Document)> DecodeSegmentDocuments(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var segments = new List<(CrystalPromptManagerSegment, XDocument)>();
        using var aes = Aes.Create();
        aes.Key = ReverseWords(PromptManagerKey);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var offset = 0;
        while (offset < bytes.Length && segments.Count < MaxSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var inflated = InflateSegment(bytes, offset, encryptor, cancellationToken, out var length);
                if (inflated is null)
                {
                    break;
                }

                var xml = Encoding.UTF8.GetString(inflated);
                var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
                segments.Add((new CrystalPromptManagerSegment(offset, length, xml), document));
                offset += length;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                break;
            }
        }

        return segments;
    }

    /// <summary>
    /// Inflates the zlib stream of the segment that starts at <paramref name="offset"/> in one pass
    /// and returns its bytes, or null when the segment is damaged, empty or larger than the cap.
    /// <paramref name="length"/> receives how many stored bytes the segment occupies.
    /// </summary>
    /// <remarks>
    /// The reader decrypts on demand and hands zlib one byte per read. zlib asks for input only
    /// after it has used all it was given and stops at its own end-of-stream marker, having checked
    /// the Adler-32 trailer of the inflated bytes, so the bytes handed out at that point are the
    /// segment's length. The trailer is confirmed once at that position; the next segment's bytes
    /// are never read.
    /// </remarks>
    private static byte[]? InflateSegment(
        byte[] bytes,
        int offset,
        ICryptoTransform encryptor,
        CancellationToken cancellationToken,
        out int length)
    {
        length = 0;
        var reader = new SegmentReader(bytes, offset, encryptor, cancellationToken);
        using var output = new MemoryStream();
        try
        {
            using var zlib = new ZLibStream(reader, CompressionMode.Decompress);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxInflatedBytes)
                {
                    return null;
                }
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        const int zlibHeaderLength = 2;
        var inflated = output.ToArray();
        if (inflated.Length == 0 ||
            reader.Consumed < zlibHeaderLength + 4 ||
            reader.LastFourBytes != Adler32(inflated))
        {
            // Input ran out before the end-of-stream marker: a truncated or damaged segment.
            return null;
        }

        length = reader.Consumed;
        return inflated;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// The decrypted bytes of one segment, produced on demand one byte per read. The keystream
    /// for each 16-byte block is the AES encryption of the previous block's ciphertext (zero for
    /// the segment's first block), so only the bytes zlib actually asks for are decrypted.
    /// </summary>
    private sealed class SegmentReader : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _start;
        private readonly ICryptoTransform _encryptor;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _state = new byte[16];
        private readonly byte[] _transformedState = new byte[16];
        private readonly byte[] _transformedKeyStream = new byte[16];
        private readonly byte[] _keyStream = new byte[16];
        private int _position;

        public SegmentReader(byte[] bytes, int start, ICryptoTransform encryptor, CancellationToken cancellationToken)
        {
            _bytes = bytes;
            _start = start;
            _position = start;
            _encryptor = encryptor;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Stored bytes handed out so far.</summary>
        public int Consumed => _position - _start;

        /// <summary>The last four decrypted bytes handed out, big-endian (zlib's Adler-32 trailer order).</summary>
        public uint LastFourBytes { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => Consumed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _bytes.Length)
            {
                return 0;
            }

            var blockOffset = Consumed % 16;
            if (blockOffset == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ReverseWords(_state, _transformedState);
                _encryptor.TransformBlock(_transformedState, 0, _transformedState.Length, _transformedKeyStream, 0);
                ReverseWords(_transformedKeyStream, _keyStream);
            }

            var cipher = _bytes[_position++];
            var plain = (byte)(cipher ^ _keyStream[blockOffset]);
            _state[blockOffset] = cipher;
            LastFourBytes = (LastFourBytes << 8) | plain;
            buffer[0] = plain;
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private sealed record PromptInfo(string Name, bool HasConstraint, IReadOnlyList<string> DefaultValues, string? PromptGroupRef, int? PromptIndex);

    private sealed record PromptGroupInfo(string Name, string? LovSourceRef, IReadOnlyList<GroupPrompt> Prompts);

    private sealed record GroupPrompt(string FieldId, string Sort);

    private sealed record LovSourceInfo(IReadOnlyList<LovField> Fields, IReadOnlyDictionary<string, LovTable> Tables);

    private sealed record LovField(string Name, DbFieldRef Value, DbFieldRef? Description, bool IsCascadingChild);

    private sealed record DbFieldRef(string TableAlias, string Name);

    private sealed record LovTable(string Name, bool IsCommand);
}

/// <summary>
/// One XML segment of a Crystal <c>PromptManager</c> stream: where it starts in the stream, how
/// many stored (encrypted and compressed) bytes it occupies and the decoded XML.
/// </summary>
public sealed record CrystalPromptManagerSegment(int Offset, int Length, string Xml);
