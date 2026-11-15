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
    internal CollectionFormatException(CollectionDiagnostic diagnostic) : base(diagnostic.ToString()) =>
        Diagnostic = diagnostic;

    internal CollectionDiagnostic Diagnostic { get; }

    internal static CollectionFormatException At(string code, string message, string? field = null) =>
        new(new CollectionDiagnostic(code, message, Field: field));
}

/// <summary>An immutable geometry tree in Project coordinates and metres.</summary>
public sealed class GeometryCollection
{
    private const double MinimumPlaneAxis = 1e-9;

    private GeometryCollection(JsonElement payload, int schemaVersion, int branchCount, int count, int geometryCount)
    {
        Payload = payload;
        SchemaVersion = schemaVersion;
        BranchCount = branchCount;
        SlotCount = count;
        GeometryCount = geometryCount;
    }

    public JsonElement Payload { get; }
    public int SchemaVersion { get; }
    public int BranchCount { get; }
    public int SlotCount { get; }
    public int GeometryCount { get; }

    public override string ToString() => $"{BranchCount} branches, {SlotCount} slots, {GeometryCount} geometries";

    public static GeometryCollection Parse(JsonElement payload)
    {
        Fields(payload, new[] { "schemaVersion", "units", "space", "branches" });
        var schemaVersion = Integer(payload.GetProperty("schemaVersion"), "schemaVersion");
        if (schemaVersion is not (1 or 2) || Text(payload.GetProperty("units"), "units") != "m"
            || Text(payload.GetProperty("space"), "space") != "project")
            throw Invalid("Expected a supported collection in Project metres.");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var branches = new List<(int[] Path, JsonElement Value)>();
        var outOfOrder = false;
        var slots = 0;
        var geometryCount = 0;
        foreach (var branch in Elements(payload.GetProperty("branches"), "branches"))
        {
            Fields(branch, new[] { "path", "items" });
            var path = Elements(branch.GetProperty("path"), "path", 1).Select(value => Integer(value, "path")).ToArray();
            if (!paths.Add(string.Join(";", path)))
                throw new CollectionFormatException(new("INVALID_COLLECTION", "Duplicate branch path.", path, Field: "path"));

            outOfOrder |= branches.Count > 0 && branches[^1].Path.AsSpan().SequenceCompareTo(path) > 0;
            branches.Add((path, branch));
            var index = 0;
            foreach (var item in Elements(branch.GetProperty("items"), "items"))
            {
                if (item.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        CheckItem(item, schemaVersion);
                        if (item.TryGetProperty("itemKey", out var keyValue))
                        {
                            var key = Text(keyValue, "itemKey");
                            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                                throw Invalid("Item keys must be nonblank and unique within a collection.", "itemKey");
                        }
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
            payload = Snapshot(branches.Select(branch => branch.Value), schemaVersion);
        }
        return new GeometryCollection(payload.Clone(), schemaVersion, branches.Count, slots, geometryCount);
    }

    internal static JsonElement Snapshot<T>(IEnumerable<T> branches, int schemaVersion) => JsonSerializer.SerializeToElement(
        new { schemaVersion, units = "m", space = "project", branches });

    private static void CheckItem(JsonElement item, int schemaVersion)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var value))
            throw Invalid("Expected a geometry object with a kind.", "kind");
        switch (Text(value, "kind"))
        {
            case "point":
                Fields(item, new[] { "kind", "position" }, "itemKey");
                Coordinates(item.GetProperty("position"), "position");
                break;
            case "line":
                Fields(item, new[] { "kind", "start", "end" }, "itemKey", "domain");
                if (Coordinates(item.GetProperty("start"), "start") == Coordinates(item.GetProperty("end"), "end"))
                    throw Invalid("A line's endpoints must differ.", "end");
                CheckDomain(item);
                break;
            case "polyline":
                Fields(item, new[] { "kind", "points", "closed" }, "itemKey", "domain");
                CheckPolyline(item);
                CheckDomain(item);
                break;
            case "plane":
                Fields(item, new[] { "kind", "origin", "normal", "direction" }, "itemKey");
                CheckPlane(item);
                break;
            case "box":
                Fields(item, new[] { "kind", "plane", "x", "y", "z" }, "itemKey");
                var plane = item.GetProperty("plane");
                Fields(plane, new[] { "origin", "normal", "direction" });
                CheckPlane(plane);
                foreach (var axis in new[] { "x", "y", "z" })
                    Interval(item.GetProperty(axis), axis);
                break;
            case "triangle-mesh":
                Fields(item, new[] { "kind", "positions", "triangles" }, "itemKey");
                CheckMesh(item);
                break;
            case "brep-step" when schemaVersion == 2:
                Fields(item, new[] { "kind", "shapeType", "step" }, "itemKey");
                if (Text(item.GetProperty("shapeType"), "shapeType") is not ("face" or "shell" or "solid"))
                    throw Invalid("Declare a STEP face, shell or solid.", "shapeType");
                if (!StepText.IsPart21(Text(item.GetProperty("step"), "step")))
                    throw CollectionFormatException.At("INVALID_STEP", "Expected a bounded STEP Part 21 document.", "step");
                break;
            default:
                throw CollectionFormatException.At("UNSUPPORTED_GEOMETRY", "This geometry kind is unavailable in the declared collection version.", "kind");
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
        var first = Coordinates(points.First(), "points[0]");
        var previous = first;
        HashSet<(double X, double Y, double Z)>? distinct = closed ? new() { first } : null;
        var index = 1;
        foreach (var value in points.Skip(1))
        {
            var current = Coordinates(value, $"points[{index}]");
            if (previous == current)
                throw Invalid("A polyline segment has zero length.", $"points[{index}]");
            distinct?.Add(current);
            previous = current;
            index++;
        }
        if (closed && (previous == first || distinct!.Count < 3))
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
        var points = Elements(item.GetProperty("positions"), "positions", 3)
            .Select((value, index) => Coordinates(value, $"positions[{index}]")).ToArray();
        var index = 0;
        foreach (var face in Elements(item.GetProperty("triangles"), "triangles", 1))
        {
            var field = $"triangles[{index++}]";
            RequireLength(face, 3, field);
            var a = Integer(face[0], field);
            var b = Integer(face[1], field);
            var c = Integer(face[2], field);
            if (a >= points.Length || b >= points.Length || c >= points.Length)
                throw CollectionFormatException.At("MESH_INDEX_OUT_OF_RANGE", "A triangle refers to a missing vertex.", field);
            var normal = Cross(Subtract(points[b], points[a]), Subtract(points[c], points[a]));
            if (normal == (0, 0, 0) || !double.IsFinite(normal.X) || !double.IsFinite(normal.Y) || !double.IsFinite(normal.Z))
                throw Invalid("A triangle must have a finite, nonzero area.", field);
        }
    }

    private static (double X, double Y, double Z) Subtract((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static (double X, double Y, double Z) Coordinates(JsonElement value, string field)
    {
        RequireLength(value, 3, field);
        return (Number(value[0], field), Number(value[1], field), Number(value[2], field));
    }

    private static void Interval(JsonElement value, string field)
    {
        RequireLength(value, 2, field);
        if (Number(value[0], field) >= Number(value[1], field))
            throw Invalid("Expected a strictly increasing interval.", field);
    }

    private static void RequireLength(JsonElement value, int count, string field)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != count)
            throw Invalid($"Expected exactly {count} values.", field);
    }

    private static JsonElement.ArrayEnumerator Elements(JsonElement value, string field, int minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum)
            throw Invalid($"Expected an array with at least {minimum} elements.", field);
        return value.EnumerateArray();
    }

    private static int Integer(JsonElement value, string field)
    {
        var number = Number(value, field);
        if (number < 0 || number > int.MaxValue || number != Math.Truncate(number))
            throw Invalid("Expected a nonnegative 32-bit integer.", field);
        return (int)number;
    }

    private static double Number(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Invalid("Expected a finite number.", field);

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Invalid("Expected a string.", field);

    private static void Fields(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if ((!required.Contains(property.Name) && !optional.Contains(property.Name)) || !seen.Add(property.Name))
                throw Invalid("Unexpected or repeated property.", property.Name);
        }
        foreach (var name in required)
        {
            if (!seen.Contains(name))
                throw Invalid("Missing required property.", name);
        }
    }

    private static CollectionFormatException Invalid(string message, string? field = null) =>
        CollectionFormatException.At("INVALID_GEOMETRY", message, field);
}
