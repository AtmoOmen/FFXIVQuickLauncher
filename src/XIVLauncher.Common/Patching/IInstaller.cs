using System.Threading;
using System.Threading.Tasks;

namespace XIVLauncher.Common.Patching;

public interface IInstaller
{
    /// <summary>Moves a file using the worker process' permissions.</summary>
    /// <param name="sourceFile">Path of the source file.</param>
    /// <param name="targetFile">New path to move the source file to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation state.</returns>
    Task MoveFile(string sourceFile, string targetFile, CancellationToken cancellationToken = default);

    /// <summary>Creates a directory using the worker process' permissions.</summary>
    /// <param name="dir">Directory to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation state.</returns>
    Task CreateDirectory(string dir, CancellationToken cancellationToken = default);

    /// <summary>Removes a directory using the worker process' permissions.</summary>
    /// <param name="dir">Directory to remove.</param>
    /// <param name="recursive">Whether to remove the directory recursively.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the operation state.</returns>
    Task RemoveDirectory(string dir, bool recursive = false, CancellationToken cancellationToken = default);
}
