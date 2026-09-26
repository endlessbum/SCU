using Xunit;

namespace SCU.Tests;

// Тесты, меняющие статический L.Current, не должны идти параллельно с остальными:
// ActionCaption у ServiceRowViewModel и другие методы читают язык напрямую.
[CollectionDefinition("LocalizationSequential", DisableParallelization = true)]
public sealed class LocalizationSequentialCollection;
