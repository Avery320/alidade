using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Torquetum.Alidade;

internal sealed record CollectionDiagnostic(string Code, string Message, int[]? Path = null,
    int? ItemIndex = null, string? Field = null, bool Warning = false)
{
    public override string ToString() =>
        (ItemIndex is { } index ? $"Item {index}: " : "") + $"{Code}: {Message}"
        + (Field is null ? "" : $" ({Field})");
}

internal sealed class CollectionFormatException : FormatException
{
    internal CollectionFormatException(CollectionDiagnostic diagnostic) : base(diagnostic.Path is { Length: > 0 } path
        ? $"Branch {{{string.Join(";", path)}}}: {diagnostic}" : diagnostic.ToString()) =>
        Diagnostic = diagnostic;

    internal CollectionDiagnostic Diagnostic { get; }

    internal static CollectionFormatException At(string code, string message, string? field = null) =>
        new(new CollectionDiagnostic(code, message, Field: field));
}

/// <summary>An immutable geometry tree in Project coordinates and metres.</summary>
public sealed class GeometryCollection
{
    internal const int MaxExchangeBytes = 32 * 1024 * 1024;
    private const double MinimumPlaneAxis = 1e-9;
    private static readonly JsonElement Contract = ReadContract();
    private static readonly JsonElement CollectionProperties = Contract.GetProperty("properties");
    private static readonly JsonElement Definitions = Contract.GetProperty("$defs");
    private static readonly string SchemaVersion = CollectionProperties.GetProperty("schemaVersion").GetProperty("const").GetString()!;
    private static readonly ObjectFields CollectionFields = ObjectFields.Read(Contract);
    private static readonly ObjectFields BranchFields = ObjectFields.Read(CollectionProperties.GetProperty("branches").GetProperty("items"));
    private static readonly ObjectFields PlaneFields = ObjectFields.Read(Definitions.GetProperty("planeFrame"));
    private static readonly Dictionary<string, ObjectFields> GeometryFields = Definitions.GetProperty("geometry")
        .GetProperty("oneOf").EnumerateArray().ToDictionary(
            shape => shape.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()!,
            ObjectFields.Read, StringComparer.Ordinal);

    private GeometryCollection(JsonElement payload, int branchCount, int count, int geometryCount)
    {
        Payload = payload;
        BranchCount = branchCount;
        SlotCount = count;
        GeometryCount = geometryCount;
    }

    public JsonElement Payload { get; }
    public int BranchCount { get; }
    public int SlotCount { get; }
    public int GeometryCount { get; }

    public override string ToString() => $"{BranchCount} branches, {SlotCount} slots, {GeometryCount} geometries";

    internal static GeometryCollection FromOwnedPayload(JsonElement payload)
    {
        CollectionFields.Check(payload);

        var source = payload.GetProperty("branches");
        var entries = Elements(source, "branches");
        var branches = new List<(int[] Path, JsonElement Value)>(source.GetArrayLength());
        var previousPath = Array.Empty<int>();
        var outOfOrder = false;
        var slots = 0;
        var geometryCount = 0;
        foreach (var branch in entries)
        {
            BranchFields.Check(branch);
            var path = Elements(branch.GetProperty("path"), "path", 1).Select(value => Integer(value, "path")).ToArray();
            var order = previousPath.AsSpan().SequenceCompareTo(path);
            if (order == 0)
                throw DuplicatePath(path);
            outOfOrder |= order > 0;
            branches.Add((path, branch));
            previousPath = path;
            var index = 0;
            foreach (var item in Elements(branch.GetProperty("items"), "items"))
            {
                if (item.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        CheckItem(item);
                        geometryCount++;
                    }
                    catch (CollectionFormatException error)
                    {
                        throw new CollectionFormatException(error.Diagnostic with { Path = path, ItemIndex = index });
                    }
                }
                index++;
            }
            slots += index;
        }

        if (outOfOrder)
        {
            branches.Sort((left, right) => left.Path.AsSpan().SequenceCompareTo(right.Path));
            for (var index = 1; index < branches.Count; index++)
                if (branches[index - 1].Path.AsSpan().SequenceEqual(branches[index].Path))
                    throw DuplicatePath(branches[index].Path);
            payload = Snapshot(branches.Select(branch => branch.Value));
        }
        return new GeometryCollection(payload, branches.Count, slots, geometryCount);
    }

    private static CollectionFormatException DuplicatePath(int[] path) =>
        new(new("INVALID_COLLECTION", "Duplicate branch path.", path, Field: "path"));

    internal static JsonElement Snapshot<T>(IEnumerable<T> branches) => JsonSerializer.SerializeToElement(
        new { schemaVersion = SchemaVersion, units = "m", space = "project", branches });

    private static void CheckItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var value))
            throw Invalid("Expected a geometry object with a kind.", "kind");
        var kind = Text(value, "kind");
        if (!GeometryFields.TryGetValue(kind, out var fields))
            throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This geometry kind is unsupported.", "kind");
        fields.Check(item);
        switch (kind)
        {
            case "point":
                Coordinates(item.GetProperty("position"), "position");
                break;
            case "line":
                if (Coordinates(item.GetProperty("start"), "start") == Coordinates(item.GetProperty("end"), "end"))
                    throw Invalid("A line's endpoints must differ.", "end");
                CheckDomain(item);
                break;
            case "polyline":
                CheckPolyline(item);
                CheckDomain(item);
                break;
            case "plane":
                CheckPlane(item);
                break;
            case "box":
                var plane = item.GetProperty("plane");
                PlaneFields.Check(plane);
                CheckPlane(plane);
                foreach (var axis in new[] { "x", "y", "z" })
                    Interval(item.GetProperty(axis), axis);
                break;
            case "triangle-mesh":
                CheckMesh(item);
                break;
            case "brep-step":
                if (!StepText.IsPart21(Text(item.GetProperty("step"), "step")))
                    throw CollectionFormatException.At("INVALID_STEP", "Expected a bounded STEP Part 21 document.", "step");
                break;
            default:
                throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This geometry kind has no Alidade converter.", "kind");
        }
    }

    private static void CheckDomain(JsonElement item)
    {
        if (item.TryGetProperty("domain", out var domain))
            Interval(domain, "domain");
    }

    private static void CheckPolyline(JsonElement item)
    {
        var closedValue = item.GetProperty("closed");
        if (closedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("Expected a Boolean closure flag.", "closed");
        var closed = closedValue.GetBoolean();
        var points = Elements(item.GetProperty("points"), "points", closed ? 3 : 2);
        points.MoveNext();
        var first = Coordinates(points.Current, "points", 0);
        var second = first;
        var previous = first;
        var hasThird = false;
        var index = 1;
        while (points.MoveNext())
        {
            var current = Coordinates(points.Current, "points", index);
            if (previous == current)
                throw Invalid("A polyline segment has zero length.", IndexedField("points", index));
            if (index == 1)
                second = current;
            hasThird |= current != first && current != second;
            previous = current;
            index++;
        }
        if (closed && (previous == first || !hasThird))
            throw Invalid("A closed polyline needs three distinct vertices and no repeated closing vertex.", "points");
    }

    private static void CheckPlane(JsonElement item)
    {
        Coordinates(item.GetProperty("origin"), "origin");
        var normal = Coordinates(item.GetProperty("normal"), "normal");
        var direction = Coordinates(item.GetProperty("direction"), "direction");
        var length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
        if (!double.IsFinite(length) || length <= MinimumPlaneAxis)
            throw Invalid("A plane needs a nonzero normal.", "normal");
        var side = Cross((normal.X / length, normal.Y / length, normal.Z / length), direction);
        var width = Math.Sqrt(side.X * side.X + side.Y * side.Y + side.Z * side.Z);
        if (!double.IsFinite(width) || width <= MinimumPlaneAxis)
            throw Invalid("A plane's direction and normal must not be parallel.", "direction");
    }

    private static void CheckMesh(JsonElement item)
    {
        var positions = item.GetProperty("positions");
        var vertices = Elements(positions, "positions", 3);
        var points = new (double X, double Y, double Z)[positions.GetArrayLength()];
        var index = 0;
        foreach (var vertex in vertices)
        {
            points[index] = Coordinates(vertex, "positions", index);
            index++;
        }

        index = 0;
        foreach (var face in Elements(item.GetProperty("triangles"), "triangles", 1))
        {
            RequireLength(face, 3, "triangles", index);
            var a = Integer(face[0], "triangles", index);
            var b = Integer(face[1], "triangles", index);
            var c = Integer(face[2], "triangles", index);
            if (a >= points.Length || b >= points.Length || c >= points.Length)
                throw CollectionFormatException.At("MESH_INDEX_OUT_OF_RANGE", "A triangle refers to a missing vertex.", IndexedField("triangles", index));
            var normal = Cross(Subtract(points[b], points[a]), Subtract(points[c], points[a]));
            if (normal == (0, 0, 0) || !double.IsFinite(normal.X) || !double.IsFinite(normal.Y) || !double.IsFinite(normal.Z))
                throw Invalid("A triangle must have a finite, nonzero area.", IndexedField("triangles", index));
            index++;
        }
    }

    private static (double X, double Y, double Z) Subtract((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Coordinates(JsonElement value, string field, int index = -1)
    {
        RequireLength(value, 3, field, index);
        return (Number(value[0], field, index), Number(value[1], field, index), Number(value[2], field, index));
    }

    private static void Interval(JsonElement value, string field)
    {
        RequireLength(value, 2, field);
        if (Number(value[0], field) >= Number(value[1], field))
            throw Invalid("Expected a strictly increasing interval.", field);
    }

    private static void RequireLength(JsonElement value, int count, string field, int index = -1)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
            throw Invalid($"Expected exactly {count} values.", IndexedField(field, index));
    }

    private static JsonElement.ArrayEnumerator Elements(JsonElement value, string field, int minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum)
            throw Invalid($"Expected an array with at least {minimum} elements.", field);
        return value.EnumerateArray();
    }

    private static int Integer(JsonElement value, string field, int index = -1)
    {
        var number = Number(value, field, index);
        if (number < 0 || number > int.MaxValue || number != Math.Truncate(number))
            throw Invalid("Expected a nonnegative 32-bit integer.", IndexedField(field, index));
        return (int)number;
    }

    private static double Number(JsonElement value, string field, int index = -1) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Invalid("Expected a finite number.", IndexedField(field, index));

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("Expected a string.", field);

    private static JsonElement ReadContract()
    {
        using var resource = typeof(GeometryCollection).Assembly
            .GetManifestResourceStream("Torquetum.Alidade.geometry-collection.schema.json")
            ?? throw new InvalidOperationException("The Tor API geometry collection schema is missing.");
        return JsonSerializer.Deserialize<JsonElement>(resource);
    }

    // Project the contract's object fields once. The geometry checks handle tuple contents in one pass.
    private sealed record ObjectFields(string[] Required, Dictionary<string, string[]?> Allowed)
    {
        internal static ObjectFields Read(JsonElement definition)
        {
            var sources = definition.TryGetProperty("$ref", out var reference)
                ? new[] { definition, Definitions.GetProperty(reference.GetString()!["#/$defs/".Length..]) }
                : new[] { definition };
            var required = sources.SelectMany(source => source.TryGetProperty("required", out var names)
                ? Strings(names) : Array.Empty<string>()).ToArray();
            // Selecting a geometry definition already validates the kind value.
            var allowed = sources.SelectMany(source => source.GetProperty("properties").EnumerateObject())
                .ToDictionary(property => property.Name,
                    property => property.Name == "kind" ? null : AllowedValues(property.Value), StringComparer.Ordinal);
            return new(required, allowed);
        }

        private static string[] Strings(JsonElement values) =>
            values.EnumerateArray().Select(value => value.GetString()!).ToArray();

        private static string[]? AllowedValues(JsonElement definition)
        {
            if (definition.TryGetProperty("const", out var literal))
                return new[] { literal.GetString()! };
            return definition.TryGetProperty("enum", out var values) ? Strings(values) : null;
        }

        internal void Check(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw Invalid("Expected an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var name = property.Name;
                if (!Allowed.TryGetValue(name, out var choices) || !seen.Add(name))
                    throw Invalid("Unexpected or repeated property.", name);
                if (choices != null && (property.Value.ValueKind != JsonValueKind.String ||
                    !choices.Contains(property.Value.GetString(), StringComparer.Ordinal)))
                    throw Invalid($"Expected {string.Join(" or ", choices)}.", name);
            }
            foreach (var name in Required)
                if (!seen.Contains(name))
                    throw Invalid("Missing required property.", name);
        }
    }

    private static string IndexedField(string field, int index) => index < 0 ? field : $"{field}[{index}]";

    private static CollectionFormatException Invalid(string message, string? field = null) =>
        CollectionFormatException.At("INVALID_GEOMETRY", message, field);
}
