using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public interface IPortableObjectStreamProvider
{
    Task<Stream?> OpenReadAsync(FileSystemEntry entry, CancellationToken cancellationToken);
}
