using System;

namespace XIVLauncher.Common.PlatformAbstractions;

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
