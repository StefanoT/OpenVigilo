using Vigilo.Core;

namespace Vigilo.Classification;

/// <summary>
/// Live classification pipeline version for persistence: the harness version composed with
/// the content-derived prompt version, so either a harness change or any prompt edit makes
/// stored messages reclassification-eligible without hand-maintained version constants.
/// </summary>
public sealed class ClassificationVersionSource(IPromptRegistry prompts) : IClassificationVersionSource
{
    public string ClassificationVersion => $"{EmailAnalysisHarness.CurrentVersion}+{prompts.CompositeVersion}";

    public string PromptVersion => prompts.CompositeVersion;
}
