namespace Vigilo.LocalAi;

public sealed class LocalAiActivityTracker
{
    private int _activeInferenceCount;

    public bool IsInferenceActive => Volatile.Read(ref _activeInferenceCount) > 0;

    public IDisposable BeginInference()
    {
        Interlocked.Increment(ref _activeInferenceCount);
        return new InferenceActivity(this);
    }

    private void EndInference() => Interlocked.Decrement(ref _activeInferenceCount);

    private sealed class InferenceActivity(LocalAiActivityTracker tracker) : IDisposable
    {
        private LocalAiActivityTracker? _tracker = tracker;

        public void Dispose() => Interlocked.Exchange(ref _tracker, null)?.EndInference();
    }
}
