using System;
using Grasshopper.Kernel;

namespace Torquetum.Nuncius;

public sealed class NuncAssemblyInfo : GH_AssemblyInfo
{
    public override Guid Id => new("f0bf8097-ecaf-4858-b36d-f09790ae7cf5");

    internal const string PluginName = "Nuncius";

    public override string Name => PluginName;

    public override string Description => "Geometry and data tree exchange with Torquetum.";

    public override string AuthorName => "AveryTsai";
}
