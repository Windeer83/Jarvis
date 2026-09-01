using Xunit;

namespace Jarvis.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfTestCollection
{
    public const string Name = "WPF";
}
