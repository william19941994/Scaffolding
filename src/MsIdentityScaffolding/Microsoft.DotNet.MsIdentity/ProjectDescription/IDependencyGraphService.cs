using NuGet.ProjectModel;

namespace Microsoft.DotNet.MsIdentity.Project
{
    internal interface IDependencyGraphService
    {
        DependencyGraphSpec? GenerateDependencyGraph();
    }
}
