namespace XIVLauncher.Dalamud;

public interface IDalamudCompatibilityCheck
{
    void EnsureCompatibility();

    class ArchitectureNotSupportedException : Exception
    {
        public ArchitectureNotSupportedException(string message)
            : base(message)
        {
        }
    }

    class NoRedistsException : Exception
    {
    }
}
